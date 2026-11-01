using Jvd.Application.Clientes;
using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Jvd.Infrastructure;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Relatorios;

/// <summary>
/// Rascunho, emissão (número sem buracos, snapshots) e consulta do Relatório de Serviço sobre SQL Server real.
/// Dados 100% SINTÉTICOS. Só com JVD_TESTES_BANCO=1.
/// </summary>
public class RelatorioServiceTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly InterceptorDeFalha _falha = new();
    private ServiceProvider _sp = null!;
    private AtorDoRelatorio _gestor = null!;
    private AtorDoRelatorio _tecnicoA = null!;
    private AtorDoRelatorio _tecnicoB = null!;

    public RelatorioServiceTests(BancoDeTeste banco) => _banco = banco;

    public async Task InitializeAsync()
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(DadosDeTeste.ConfiguracaoBase(_banco.Conexao)).Build();
        _sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuracao)
            .AddSingleton<TimeProvider>(_relogio)
            .AddSingleton<IInterceptor>(_falha)
            .AddJvdInfrastructure(configuracao)
            .BuildServiceProvider();

        _gestor = new AtorDoRelatorio((await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor)).Id, true);
        _tecnicoA = new AtorDoRelatorio((await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico)).Id, false);
        _tecnicoB = new AtorDoRelatorio((await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico)).Id, false);
        await CadastrosDeTeste.GarantirEmissorAsync(_sp, _gestor.Id);
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

    /// <summary>Quando armado, faz a gravação da emissão falhar DEPOIS de o contador ter sido incrementado.</summary>
    private sealed class InterceptorDeFalha : SaveChangesInterceptor
    {
        public volatile bool Armado;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> resultado, CancellationToken ct = default)
        {
            if (Armado && eventData.Context!.ChangeTracker.Entries<RelatorioEvento>()
                    .Any(e => e.State == EntityState.Added && e.Entity.Tipo == TipoEventoRelatorio.Emitido))
            {
                throw new TimeoutException("falha simulada ao gravar a emissão");
            }

            return base.SavingChangesAsync(eventData, resultado, ct);
        }
    }

    // ---------- apoio ----------

    private async Task<T> Servico<T>(Func<IRelatorioService, Task<T>> acao)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IRelatorioService>());
    }

    private async Task<T> Banco<T>(Func<JvdDbContext, Task<T>> consulta)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await consulta(escopo.ServiceProvider.GetRequiredService<JvdDbContext>());
    }

    private record Par(Guid ClienteId, Guid EquipamentoId);

    private async Task<Par> NovoParAsync(bool comDocumento = true)
    {
        var cliente = await CadastrosDeTeste.NovoClienteAsync(_sp, _gestor.Id, comDocumento);
        var equipamento = await CadastrosDeTeste.NovoEquipamentoAsync(_sp, _gestor.Id, cliente.Id);
        return new Par(cliente.Id, equipamento.Id);
    }

    private static DadosDoRelatorio Dados(Par par, string? servico = "Troca de peça e ajuste", decimal total = 100m, decimal desconto = 10m,
        int garantia = 30, Guid? tecnico = null, TipoServico tipo = TipoServico.Corretiva) =>
        new(par.ClienteId, par.EquipamentoId, tecnico, tipo, new DateOnly(2026, 10, 30), servico, "Obs", new DateOnly(2026, 1, 2), "CERT-1",
            "SELO-9", garantia, total, desconto, null);

    private async Task<RelatorioDetalhe> RascunhoAsync(AtorDoRelatorio ator, Par? par = null)
    {
        par ??= await NovoParAsync();
        var r = await Servico(s => s.CriarRascunhoAsync(Dados(par), ator));
        Assert.True(r.Sucesso, string.Join(";", r.Erros.Select(e => e.Mensagem)));
        return r.Valor!;
    }

    private async Task<RelatorioDetalhe> EmitidoAsync(AtorDoRelatorio ator, Par? par = null)
    {
        var rascunho = await RascunhoAsync(ator, par);
        var r = await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, ator));
        Assert.True(r.Sucesso, string.Join(";", r.Erros.Select(e => e.Mensagem)));
        return r.Valor!;
    }

    private static string? Codigo(Resultado r) => r.Erros.FirstOrDefault()?.Codigo;

    // ---------- rascunho ----------

    [FactBanco]
    public async Task CriarRascunho_NaoTemNumero_CalculaTotalGeral_GeraGarantiaPorExtenso_ERegistraEvento()
    {
        var r = await RascunhoAsync(_tecnicoA);

        Assert.Equal(StatusRelatorio.Rascunho, r.Status);
        Assert.Null(r.Numero);
        Assert.Null(r.NumeroFormatado);
        Assert.Equal(90m, r.TotalGeral);
        Assert.Equal("30 dias (trinta)", r.GarantiaExtenso);
        Assert.Equal(_tecnicoA.Id, r.TecnicoId);
        Assert.Null(r.Cliente);   // snapshots só existem depois da emissão
        Assert.False(string.IsNullOrEmpty(r.Versao));

        var evento = await Banco(db => db.Eventos.AsNoTracking().SingleAsync(e => e.RelatorioId == r.Id));
        Assert.Equal(TipoEventoRelatorio.Criado, evento.Tipo);
        Assert.Equal(_tecnicoA.Id, evento.UsuarioId);
    }

    [FactBanco]
    public async Task CriarRascunho_SoOGestorDefineOutroExecutor_EOExecutorPrecisaEstarAtivo()
    {
        var par = await NovoParAsync();

        var tecnicoTentando = await Servico(s => s.CriarRascunhoAsync(Dados(par, tecnico: _tecnicoB.Id), _tecnicoA));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(tecnicoTentando));

        var gestorDefine = await Servico(s => s.CriarRascunhoAsync(Dados(par, tecnico: _tecnicoB.Id), _gestor));
        Assert.True(gestorDefine.Sucesso);
        Assert.Equal(_tecnicoB.Id, gestorDefine.Valor!.TecnicoId);

        var inexistente = await Servico(s => s.CriarRascunhoAsync(Dados(par, tecnico: Guid.NewGuid()), _gestor));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(inexistente));
    }

    [FactBanco]
    public async Task CriarRascunho_ClienteOuEquipamentoInativoOuDeOutroCliente_EhRecusado()
    {
        var par = await NovoParAsync();
        var outro = await NovoParAsync();

        var deOutroCliente = await Servico(s => s.CriarRascunhoAsync(Dados(par) with { EquipamentoId = outro.EquipamentoId }, _tecnicoA));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(deOutroCliente));

        var semCliente = await Servico(s => s.CriarRascunhoAsync(Dados(par) with { ClienteId = Guid.NewGuid() }, _tecnicoA));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(semCliente));

        await using (var escopo = _sp.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<IClienteService>().DesativarAsync(par.ClienteId, _gestor.Id);
        }

        var clienteInativo = await Servico(s => s.CriarRascunhoAsync(Dados(par), _tecnicoA));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(clienteInativo));
    }

    [FactBanco]
    public async Task CriarRascunho_ValoresEDadosInvalidos_ListaOsErros_SemCriarNada()
    {
        var par = await NovoParAsync();
        var invalidos = new[]
        {
            Dados(par, total: -1m, desconto: 0m),
            Dados(par, total: 100m, desconto: 100.01m),            // desconto maior que o total
            Dados(par, total: 10.005m),                              // 3 casas
            Dados(par, total: 10_000_000_000m),                      // acima do máximo
            Dados(par, garantia: -1),
            Dados(par, garantia: 10000),
            Dados(par, servico: new string('x', 4001)),
            Dados(par) with { TipoServico = (TipoServico)99 },
            Dados(par) with { Observacoes = new string('o', 2001) },
            Dados(par) with { NumeroSeloReparador = new string('s', 61) },
            Dados(par) with { VerificacaoAnteriorCertificado = new string('c', 61) },
            Dados(par) with { ClienteId = Guid.Empty }
        };

        foreach (var dados in invalidos)
        {
            var r = await Servico(s => s.CriarRascunhoAsync(dados, _tecnicoA));
            Assert.False(r.Sucesso);
            Assert.All(r.Erros, e => Assert.Equal(CodigosDeErro.Validacao, e.Codigo));
        }
    }

    [FactBanco]
    public async Task CriarRascunho_DescontoIgualAoTotal_EGarantiaZero_SaoValidos()
    {
        var par = await NovoParAsync();

        var r = await Servico(s => s.CriarRascunhoAsync(Dados(par, total: 50m, desconto: 50m, garantia: 0), _tecnicoA));

        Assert.True(r.Sucesso);
        Assert.Equal(0m, r.Valor!.TotalGeral);
        Assert.Equal("0 dias (zero)", r.Valor.GarantiaExtenso);
    }

    [FactBanco]
    public async Task CriarRascunho_ServicoDoCatalogoInexistente_EhRecusado()
    {
        var par = await NovoParAsync();

        var r = await Servico(s => s.CriarRascunhoAsync(Dados(par) with { CatalogoServicoId = Guid.NewGuid() }, _tecnicoA));

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    [FactBanco]
    public async Task AtualizarRascunho_Altera_ExigeVersao_E409ComVersaoVelha()
    {
        var rascunho = await RascunhoAsync(_tecnicoA);
        var par = new Par(rascunho.ClienteId, rascunho.EquipamentoId);

        var nova = Dados(par, servico: "Serviço corrigido", total: 200m, desconto: 0m);
        var r = await Servico(s => s.AtualizarRascunhoAsync(rascunho.Id, nova, rascunho.Versao, _tecnicoA));
        Assert.True(r.Sucesso);
        Assert.Equal("Serviço corrigido", r.Valor!.ServicoExecutado);
        Assert.Equal(200m, r.Valor.TotalGeral);
        Assert.NotEqual(rascunho.Versao, r.Valor.Versao);

        var velha = await Servico(s => s.AtualizarRascunhoAsync(rascunho.Id, nova, rascunho.Versao, _tecnicoA));
        Assert.Equal(CodigosDeErro.Conflito, Codigo(velha));

        var malformada = await Servico(s => s.AtualizarRascunhoAsync(rascunho.Id, nova, "isto não é base64", _tecnicoA));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(malformada));
    }

    [FactBanco]
    public async Task AtualizarRascunho_ExecutorNulo_MantemOExecutor_ETecnicoNaoTrocaOExecutor()
    {
        var par = await NovoParAsync();
        var criado = (await Servico(s => s.CriarRascunhoAsync(Dados(par, tecnico: _tecnicoA.Id), _gestor))).Valor!;

        var gestorEditaSemExecutor = await Servico(s => s.AtualizarRascunhoAsync(criado.Id, Dados(par, servico: "Novo texto"), criado.Versao, _gestor));
        Assert.True(gestorEditaSemExecutor.Sucesso);
        Assert.Equal(_tecnicoA.Id, gestorEditaSemExecutor.Valor!.TecnicoId);

        var tecnicoTrocando = await Servico(s => s.AtualizarRascunhoAsync(criado.Id, Dados(par, tecnico: _tecnicoB.Id), gestorEditaSemExecutor.Valor.Versao, _tecnicoA));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(tecnicoTrocando));
    }

    [FactBanco]
    public async Task Rascunho_DeOutroTecnico_EhNaoEncontrado_ParaTodasAsOperacoes_EOGestorEnxerga()
    {
        var rascunho = await RascunhoAsync(_tecnicoA);
        var par = new Par(rascunho.ClienteId, rascunho.EquipamentoId);

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.ObterAsync(rascunho.Id, _tecnicoB))));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.AtualizarRascunhoAsync(rascunho.Id, Dados(par), rascunho.Versao, _tecnicoB))));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.DescartarRascunhoAsync(rascunho.Id, _tecnicoB))));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoB))));

        Assert.True((await Servico(s => s.ObterAsync(rascunho.Id, _gestor))).Sucesso);
    }

    [FactBanco]
    public async Task DescartarRascunho_Apaga_ComOEvento_EEmitidoNaoSeApaga()
    {
        var rascunho = await RascunhoAsync(_tecnicoA);

        Assert.True((await Servico(s => s.DescartarRascunhoAsync(rascunho.Id, _tecnicoA))).Sucesso);

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.ObterAsync(rascunho.Id, _gestor))));
        Assert.Equal(0, await Banco(db => db.Eventos.CountAsync(e => e.RelatorioId == rascunho.Id)));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.DescartarRascunhoAsync(rascunho.Id, _tecnicoA))));

        var emitido = await EmitidoAsync(_tecnicoA);
        Assert.Equal(CodigosDeErro.RelatorioNaoERascunho, Codigo(await Servico(s => s.DescartarRascunhoAsync(emitido.Id, _gestor))));
    }

    // ---------- emissão ----------

    [FactBanco]
    public async Task Emitir_AtribuiONumeroSequencial_ComSeisDigitos_EGravaOsSnapshots()
    {
        var primeiro = await EmitidoAsync(_tecnicoA);
        var segundo = await EmitidoAsync(_tecnicoB);

        Assert.Equal(StatusRelatorio.Emitido, primeiro.Status);
        Assert.Equal(primeiro.Numero + 1, segundo.Numero);
        Assert.Equal(primeiro.Numero!.Value.ToString("D6"), primeiro.NumeroFormatado);
        Assert.Equal(6, primeiro.NumeroFormatado!.Length);
        Assert.Equal(0, primeiro.Revisao);
        Assert.NotNull(primeiro.EmitidoEm);
        Assert.Equal(_tecnicoA.Id, primeiro.EmitidoPor);
        Assert.NotNull(primeiro.Cliente!.Documento);
        Assert.Equal("Balança sintética", primeiro.Equipamento!.Descricao);
        Assert.Equal("Empresa Sintética Ltda", primeiro.Emissor!.RazaoSocial);
        Assert.NotNull(primeiro.Emissor.ResponsavelTecnicoCpf);

        var evento = await Banco(db => db.Eventos.AsNoTracking().SingleAsync(e => e.RelatorioId == primeiro.Id && e.Tipo == TipoEventoRelatorio.Emitido));
        Assert.Equal(_tecnicoA.Id, evento.UsuarioId);
        Assert.DoesNotContain(primeiro.Cliente.Nome, evento.Detalhe ?? "");
    }

    [FactBanco]
    public async Task Emitido_MantemOsDadosDoMomento_MesmoQueOCadastroMude()
    {
        var par = await NovoParAsync();
        var emitido = await EmitidoAsync(_tecnicoA, par);
        var nomeOriginal = emitido.Cliente!.Nome;

        await using (var escopo = _sp.CreateAsyncScope())
        {
            var clientes = escopo.ServiceProvider.GetRequiredService<IClienteService>();
            var atual = (await clientes.ObterAsync(par.ClienteId, true)).Valor!;
            var dados = new DadosDoCliente("Nome Novo Do Cliente", null, atual.Documento, null, null, null, null, null, null, null, null, null);
            Assert.True((await clientes.AtualizarAsync(par.ClienteId, dados, atual.Versao, _gestor.Id, true)).Sucesso);
        }

        var depois = (await Servico(s => s.ObterAsync(emitido.Id, _tecnicoA))).Valor!;
        Assert.Equal(nomeOriginal, depois.ClienteNome);
        Assert.Equal(nomeOriginal, depois.Cliente!.Nome);

        var lista = await Servico(s => s.ListarAsync(new FiltroDeRelatorios(par.ClienteId, null, null, null, null, null, null, null), _gestor));
        Assert.Equal(nomeOriginal, Assert.Single(lista.Itens).ClienteNome);
    }

    [FactBanco]
    public async Task Emitir_ClienteSemDocumento_EBloqueado_ComCodigoEstavel_ENaoConsomeNumero()
    {
        var semDocumento = await NovoParAsync(comDocumento: false);
        var rascunho = await RascunhoAsync(_tecnicoA, semDocumento);   // rascunho pode

        var r = await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoA));

        Assert.Equal(CodigosDeErro.ClienteSemDocumento, Codigo(r));
        Assert.Equal(StatusRelatorio.Rascunho, (await Servico(s => s.ObterAsync(rascunho.Id, _gestor))).Valor!.Status);
    }

    [FactBanco]
    public async Task Emitir_ServicoExecutadoVazio_EhIncompleto()
    {
        var par = await NovoParAsync();
        var rascunho = (await Servico(s => s.CriarRascunhoAsync(Dados(par, servico: "   "), _tecnicoA))).Valor!;

        var r = await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoA));

        Assert.Equal(CodigosDeErro.RelatorioIncompleto, Codigo(r));
    }

    [FactBanco]
    public async Task Emitir_ClienteOuEquipamentoDesativadosDepoisDoRascunho_EhRecusado()
    {
        var par = await NovoParAsync();
        var rascunho = await RascunhoAsync(_tecnicoA, par);
        await using (var escopo = _sp.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<IClienteService>().DesativarAsync(par.ClienteId, _gestor.Id);
        }

        var r = await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoA));

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    [FactBanco]
    public async Task Emitir_VersaoVelha_Da409_ENaoConsomeNumero()
    {
        var rascunho = await RascunhoAsync(_tecnicoA);
        var par = new Par(rascunho.ClienteId, rascunho.EquipamentoId);
        var editado = (await Servico(s => s.AtualizarRascunhoAsync(rascunho.Id, Dados(par, servico: "Mudou"), rascunho.Versao, _tecnicoA))).Valor!;
        var antes = await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync());

        var velha = await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoA));

        Assert.Equal(CodigosDeErro.Conflito, Codigo(velha));
        Assert.Equal(antes, await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync()));
        Assert.True((await Servico(s => s.EmitirAsync(rascunho.Id, editado.Versao, _tecnicoA))).Sucesso);
    }

    [FactBanco]
    public async Task Emitir_DeNovo_EhRecusado_ComoNaoERascunho()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        var r = await Servico(s => s.EmitirAsync(emitido.Id, emitido.Versao, _tecnicoA));

        Assert.Equal(CodigosDeErro.RelatorioNaoERascunho, Codigo(r));
    }

    [FactBanco]
    public async Task Emitir_AtualizarUmEmitido_EhRecusado_ComoNaoERascunho()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        var par = new Par(emitido.ClienteId, emitido.EquipamentoId);

        var r = await Servico(s => s.AtualizarRascunhoAsync(emitido.Id, Dados(par, servico: "Alteração direta"), emitido.Versao, _gestor));

        Assert.Equal(CodigosDeErro.RelatorioNaoERascunho, Codigo(r));
        Assert.Equal(emitido.ServicoExecutado, (await Servico(s => s.ObterAsync(emitido.Id, _gestor))).Valor!.ServicoExecutado);
    }

    [FactBanco]
    public async Task Emitir_FalhaAoGravar_DesfazOIncrementoDoContador_SemBuraco()
    {
        var rascunho = await RascunhoAsync(_tecnicoA);
        var antes = await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync());

        _falha.Armado = true;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoA)));
        }
        finally
        {
            _falha.Armado = false;
        }

        Assert.Equal(antes, await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync()));
        Assert.Equal(StatusRelatorio.Rascunho, (await Servico(s => s.ObterAsync(rascunho.Id, _gestor))).Valor!.Status);

        var emitido = await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoA));
        Assert.True(emitido.Sucesso);
        Assert.Equal(antes + 1, emitido.Valor!.Numero);   // o número que "falhou" foi reaproveitado: sem buraco
    }

    [FactBanco]
    public async Task Emitir_VariosRascunhosEmParalelo_NumerosDistintosEContinuos()
    {
        var rascunhos = new List<RelatorioDetalhe>();
        for (var i = 0; i < 8; i++) rascunhos.Add(await RascunhoAsync(i % 2 == 0 ? _tecnicoA : _tecnicoB));
        var antes = await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync());

        var resultados = await Task.WhenAll(rascunhos.Select(r =>
            Task.Run(() => Servico(s => s.EmitirAsync(r.Id, r.Versao, r.TecnicoId == _tecnicoA.Id ? _tecnicoA : _tecnicoB)))));

        Assert.All(resultados, r => Assert.True(r.Sucesso, string.Join(";", r.Erros.Select(e => e.Mensagem))));
        var numeros = resultados.Select(r => r.Valor!.Numero!.Value).Order().ToArray();
        Assert.Equal(Enumerable.Range(antes + 1, 8), numeros);
        Assert.Equal(antes + 8, await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync()));
    }

    [FactBanco]
    public async Task Emitir_OMesmoRascunhoEmParalelo_SoUmaVence_ESoUmNumeroEConsumido()
    {
        var rascunho = await RascunhoAsync(_tecnicoA);
        var antes = await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, _tecnicoA)))));

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        Assert.All(resultados.Where(r => !r.Sucesso),
            r => Assert.Contains(Codigo(r), new[] { CodigosDeErro.Conflito, CodigosDeErro.RelatorioNaoERascunho }));
        Assert.Equal(antes + 1, await Banco(db => db.Contadores.AsNoTracking().Select(c => c.Ultimo).SingleAsync()));
    }

    [FactBanco]
    public async Task Emitir_SemResponsavelTecnicoNoEmissor_EBloqueado()
    {
        // O emissor é um registro único: este teste usa um banco só dele.
        await using var banco = await BancoDeTeste.CriarAsync();
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(DadosDeTeste.ConfiguracaoBase(banco.Conexao)).Build();
        await using var sp = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuracao)
            .AddSingleton<TimeProvider>(_relogio).AddJvdInfrastructure(configuracao).BuildServiceProvider();
        var gestor = new AtorDoRelatorio((await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor)).Id, true);
        var cliente = await CadastrosDeTeste.NovoClienteAsync(sp, gestor.Id);
        var equipamento = await CadastrosDeTeste.NovoEquipamentoAsync(sp, gestor.Id, cliente.Id);
        var par = new Par(cliente.Id, equipamento.Id);

        async Task<Resultado<RelatorioDetalhe>> Emitir()
        {
            await using var escopo = sp.CreateAsyncScope();
            var servico = escopo.ServiceProvider.GetRequiredService<IRelatorioService>();
            var rascunho = (await servico.CriarRascunhoAsync(Dados(par), gestor)).Valor!;
            return await servico.EmitirAsync(rascunho.Id, rascunho.Versao, gestor);
        }

        Assert.Equal(CodigosDeErro.EmissorIncompleto, Codigo(await Emitir()));              // emissor ainda não configurado

        await CadastrosDeTeste.GarantirEmissorAsync(sp, gestor.Id, comResponsavelTecnico: false);
        Assert.Equal(CodigosDeErro.EmissorIncompleto, Codigo(await Emitir()));              // configurado, mas sem responsável técnico
    }

    // ---------- consulta ----------

    [FactBanco]
    public async Task Listar_TecnicoVeSoOsSeus_GestorVeTodos_EOFiltroDeExecutorSoValeParaOGestor()
    {
        var marca = await NovoParAsync();
        await Servico(s => s.CriarRascunhoAsync(Dados(marca), _tecnicoA));
        await Servico(s => s.CriarRascunhoAsync(Dados(marca), _tecnicoB));
        var filtro = new FiltroDeRelatorios(marca.ClienteId, null, null, null, null, null, null, null);

        var deA = await Servico(s => s.ListarAsync(filtro, _tecnicoA));
        var deB = await Servico(s => s.ListarAsync(filtro, _tecnicoB));
        var gestor = await Servico(s => s.ListarAsync(filtro, _gestor));
        var tecnicoTentandoVerOutro = await Servico(s => s.ListarAsync(filtro with { TecnicoId = _tecnicoB.Id }, _tecnicoA));
        var gestorFiltrando = await Servico(s => s.ListarAsync(filtro with { TecnicoId = _tecnicoB.Id }, _gestor));

        Assert.Equal(1, deA.Total);
        Assert.All(deA.Itens, i => Assert.Equal(_tecnicoA.Id, i.TecnicoId));
        Assert.Equal(1, deB.Total);
        Assert.Equal(2, gestor.Total);
        Assert.Equal([_tecnicoA.Id], tecnicoTentandoVerOutro.Itens.Select(i => i.TecnicoId).Distinct().ToArray());
        Assert.Equal([_tecnicoB.Id], gestorFiltrando.Itens.Select(i => i.TecnicoId).ToArray());
    }

    [FactBanco]
    public async Task Listar_FiltraPorStatusTipoNumeroEPeriodo_EPagina()
    {
        var par = await NovoParAsync();
        var emitido = await EmitidoAsync(_tecnicoA, par);
        await Servico(s => s.CriarRascunhoAsync(Dados(par, tipo: TipoServico.Preventiva), _tecnicoA));
        var basico = new FiltroDeRelatorios(par.ClienteId, null, null, null, null, null, null, null);

        Assert.Equal(2, (await Servico(s => s.ListarAsync(basico, _gestor))).Total);
        Assert.Equal(emitido.Id, Assert.Single((await Servico(s => s.ListarAsync(basico with { Status = StatusRelatorio.Emitido }, _gestor))).Itens).Id);
        Assert.Single((await Servico(s => s.ListarAsync(basico with { Status = StatusRelatorio.Rascunho }, _gestor))).Itens);
        Assert.Single((await Servico(s => s.ListarAsync(basico with { Tipo = TipoServico.Preventiva }, _gestor))).Itens);
        Assert.Single((await Servico(s => s.ListarAsync(basico with { Numero = emitido.Numero }, _gestor))).Itens);
        Assert.Equal(2, (await Servico(s => s.ListarAsync(basico with { De = new DateOnly(2026, 10, 30), Ate = new DateOnly(2026, 10, 30) }, _gestor))).Total);
        Assert.Empty((await Servico(s => s.ListarAsync(basico with { De = new DateOnly(2026, 11, 1) }, _gestor))).Itens);

        var pagina2 = await Servico(s => s.ListarAsync(basico with { Pagina = 2, Tamanho = 1 }, _gestor));
        Assert.Equal(2, pagina2.Total);
        Assert.Single(pagina2.Itens);
    }

    [FactBanco]
    public async Task Listar_LimitaOTamanhoDaPaginaACem()
    {
        var pagina = await Servico(s => s.ListarAsync(new FiltroDeRelatorios(null, null, null, null, null, null, null, null, Pagina: -2, Tamanho: 5000), _gestor));

        Assert.Equal(1, pagina.Pagina);
        Assert.Equal(100, pagina.Tamanho);
    }

    // ---------- integridade no banco ----------

    [FactBanco]
    public async Task Banco_RecusaRascunhoComNumero_EmitidoSemNumero_ENumeroRepetido()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        var outro = await RascunhoAsync(_tecnicoA);

        // número repetido (índice único)
        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            var r = await db.Relatorios.SingleAsync(x => x.Id == outro.Id);
            r.Status = StatusRelatorio.Emitido;
            r.Numero = emitido.Numero;
            return await db.SaveChangesAsync();
        }));

        // rascunho com número (check)
        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            var r = await db.Relatorios.SingleAsync(x => x.Id == outro.Id);
            r.Numero = 999_999;
            return await db.SaveChangesAsync();
        }));

        // emitido sem número (check)
        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            var r = await db.Relatorios.SingleAsync(x => x.Id == emitido.Id);
            r.Numero = null;
            return await db.SaveChangesAsync();
        }));

        // total geral inconsistente (check)
        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            var r = await db.Relatorios.SingleAsync(x => x.Id == outro.Id);
            r.TotalGeral = r.Total + 1;
            return await db.SaveChangesAsync();
        }));
    }
}
