using Jvd.Application.Clientes;
using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Jvd.Infrastructure;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Relatorios;

/// <summary>
/// Retificação (com histórico) e cancelamento do Relatório de Serviço sobre SQL Server real. Dados 100% SINTÉTICOS.
/// Só com JVD_TESTES_BANCO=1.
/// </summary>
public class RelatorioRetificacaoCancelamentoTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _sp = null!;
    private AtorDoRelatorio _gestor = null!;
    private AtorDoRelatorio _tecnicoA = null!;
    private AtorDoRelatorio _tecnicoB = null!;

    public RelatorioRetificacaoCancelamentoTests(BancoDeTeste banco) => _banco = banco;

    public async Task InitializeAsync()
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(DadosDeTeste.ConfiguracaoBase(_banco.Conexao)).Build();
        _sp = new ServiceCollection()
            .AddLogging().AddSingleton<IConfiguration>(configuracao).AddSingleton<TimeProvider>(_relogio)
            .AddJvdInfrastructure(configuracao).BuildServiceProvider();

        _gestor = new AtorDoRelatorio((await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor)).Id, true);
        _tecnicoA = new AtorDoRelatorio((await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico)).Id, false);
        _tecnicoB = new AtorDoRelatorio((await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico)).Id, false);
        await CadastrosDeTeste.GarantirEmissorAsync(_sp, _gestor.Id);
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

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

    private async Task<Par> NovoParAsync()
    {
        var cliente = await CadastrosDeTeste.NovoClienteAsync(_sp, _gestor.Id);
        var equipamento = await CadastrosDeTeste.NovoEquipamentoAsync(_sp, _gestor.Id, cliente.Id);
        return new Par(cliente.Id, equipamento.Id);
    }

    private async Task<RelatorioDetalhe> EmitidoAsync(AtorDoRelatorio ator, Par? par = null)
    {
        par ??= await NovoParAsync();
        var dados = new DadosDoRelatorio(par.ClienteId, par.EquipamentoId, null, TipoServico.Corretiva, new DateOnly(2026, 10, 30),
            "Troca de peça", "Obs", new DateOnly(2026, 1, 2), "CERT-1", "SELO-9", 30, 100m, 10m, null);
        var rascunho = (await Servico(s => s.CriarRascunhoAsync(dados, ator))).Valor!;
        var emitido = await Servico(s => s.EmitirAsync(rascunho.Id, rascunho.Versao, ator));
        Assert.True(emitido.Sucesso, string.Join(";", emitido.Erros.Select(e => e.Mensagem)));
        return emitido.Valor!;
    }

    /// <summary>Retificação igual ao relatório, com as mudanças pedidas.</summary>
    private static DadosDaRetificacao Retificacao(RelatorioDetalhe r, string? motivo = "Correção solicitada", Func<DadosDaRetificacao, DadosDaRetificacao>? muda = null)
    {
        var dados = new DadosDaRetificacao(motivo, r.TipoServico, r.DataServico, r.ServicoExecutado, r.Observacoes,
            r.VerificacaoAnteriorData, r.VerificacaoAnteriorCertificado, r.NumeroSeloReparador, r.GarantiaDias, r.Total, r.Desconto,
            r.CatalogoServicoId, false);
        return muda is null ? dados : muda(dados);
    }

    private static string? Codigo(Resultado r) => r.Erros.FirstOrDefault()?.Codigo;

    private async Task<RelatorioDetalhe> Detalhe(Guid id) => (await Servico(s => s.ObterAsync(id, _gestor))).Valor!;

    // ---------- retificação ----------

    [FactBanco]
    public async Task Retificar_MantemONumero_IncrementaARevisao_ERecalculaOTotalEOExtenso()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        var r = await Servico(s => s.RetificarAsync(emitido.Id,
            Retificacao(emitido, muda: d => d with { ServicoExecutado = "Serviço corrigido", Total = 150m, GarantiaDias = 90 }),
            emitido.Versao, _tecnicoA));

        Assert.True(r.Sucesso, string.Join(";", r.Erros.Select(e => e.Mensagem)));
        var novo = r.Valor!;
        Assert.Equal(emitido.Numero, novo.Numero);
        Assert.Equal(emitido.NumeroFormatado, novo.NumeroFormatado);
        Assert.Equal(StatusRelatorio.Emitido, novo.Status);
        Assert.Equal(1, novo.Revisao);
        Assert.Equal("Serviço corrigido", novo.ServicoExecutado);
        Assert.Equal(140m, novo.TotalGeral);   // 150 − 10
        Assert.Equal("90 dias (noventa)", novo.GarantiaExtenso);
        Assert.NotEqual(emitido.Versao, novo.Versao);
    }

    [FactBanco]
    public async Task Retificar_GravaOHistorico_SoComOsCamposQueMudaram_ComMotivoAtorEQuando()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        _relogio.Avancar(TimeSpan.FromMinutes(7));

        await Servico(s => s.RetificarAsync(emitido.Id,
            Retificacao(emitido, "Valor digitado errado", d => d with { Total = 150m }), emitido.Versao, _tecnicoA));

        var historico = (await Servico(s => s.ListarRevisoesAsync(emitido.Id, _tecnicoA))).Valor!;
        var revisao = Assert.Single(historico);
        Assert.Equal(1, revisao.Revisao);
        Assert.Equal("Valor digitado errado", revisao.Motivo);
        Assert.Equal(_tecnicoA.Id, revisao.RetificadoPor);
        Assert.False(string.IsNullOrEmpty(revisao.RetificadoPorNome));
        Assert.Equal(new DateTime(2026, 10, 31, 12, 7, 0, DateTimeKind.Utc), DateTime.SpecifyKind(revisao.RetificadoEm, DateTimeKind.Utc));

        // só total e totalGeral mudaram: nada de observações, garantia, etc.
        Assert.Equal(["total", "totalGeral"], revisao.DadosAnteriores.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(100m, revisao.DadosAnteriores.GetProperty("total").GetDecimal());
        Assert.Equal(150m, revisao.DadosNovos.GetProperty("total").GetDecimal());
        Assert.Equal(90m, revisao.DadosAnteriores.GetProperty("totalGeral").GetDecimal());
        Assert.Equal(140m, revisao.DadosNovos.GetProperty("totalGeral").GetDecimal());
    }

    [FactBanco]
    public async Task Retificar_VariasVezes_GeraRevisoesEmOrdem_ComOMesmoNumero()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        var primeira = (await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, "Primeira", d => d with { Observacoes = "Nova obs" }), emitido.Versao, _tecnicoA))).Valor!;
        var segunda = (await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(primeira, "Segunda", d => d with { NumeroSeloReparador = "SELO-10" }), primeira.Versao, _gestor))).Valor!;

        Assert.Equal(2, segunda.Revisao);
        Assert.Equal(emitido.Numero, segunda.Numero);
        var historico = (await Servico(s => s.ListarRevisoesAsync(emitido.Id, _gestor))).Valor!;
        Assert.Equal([1, 2], historico.Select(h => h.Revisao).ToArray());
        Assert.Equal(["Primeira", "Segunda"], historico.Select(h => h.Motivo).ToArray());
        Assert.Equal(["observacoes"], historico[0].DadosNovos.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["numeroSeloReparador"], historico[1].DadosNovos.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [FactBanco]
    public async Task Retificar_RegistraEvento_SemOMotivoNemDadosPessoais()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, "MOTIVO-RESERVADO", d => d with { Total = 120m }), emitido.Versao, _tecnicoA));

        var evento = await Banco(db => db.Eventos.AsNoTracking().SingleAsync(e => e.RelatorioId == emitido.Id && e.Tipo == TipoEventoRelatorio.Retificado));

        Assert.Equal(_tecnicoA.Id, evento.UsuarioId);
        Assert.Equal("Retificado (revisão 1).", evento.Detalhe);
        Assert.DoesNotContain("RESERVADO", evento.Detalhe);
    }

    [FactBanco]
    public async Task Retificar_MotivoObrigatorio_AteQuinhentosCaracteres_SemMudarNada()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        foreach (var motivo in new string?[] { null, "", "   ", new string('m', 501) })
        {
            var r = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, motivo, d => d with { Total = 200m }), emitido.Versao, _tecnicoA));
            Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
        }

        var intacto = await Detalhe(emitido.Id);
        Assert.Equal(0, intacto.Revisao);
        Assert.Equal(emitido.Versao, intacto.Versao);
        Assert.Empty((await Servico(s => s.ListarRevisoesAsync(emitido.Id, _gestor))).Valor!);

        var limite = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, new string('m', 500), d => d with { Total = 200m }), emitido.Versao, _tecnicoA));
        Assert.True(limite.Sucesso);
    }

    [FactBanco]
    public async Task Retificar_SemNenhumaDiferenca_EhRecusado()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        var r = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido), emitido.Versao, _tecnicoA));

        Assert.Equal(CodigosDeErro.RetificacaoSemAlteracao, Codigo(r));
        Assert.Equal(0, (await Detalhe(emitido.Id)).Revisao);
    }

    [FactBanco]
    public async Task Retificar_DadosInvalidos_SaoRecusados_SemMudarNada()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        var invalidas = new Func<DadosDaRetificacao, DadosDaRetificacao>[]
        {
            d => d with { Desconto = 999m },                  // desconto maior que o total
            d => d with { Total = -1m },
            d => d with { Total = 10.005m },
            d => d with { GarantiaDias = 10000 },
            d => d with { GarantiaDias = -1 },
            d => d with { ServicoExecutado = "   " },         // emitido continua com o serviço preenchido
            d => d with { ServicoExecutado = new string('x', 4001) },
            d => d with { TipoServico = (TipoServico)99 },
            d => d with { CatalogoServicoId = Guid.NewGuid() }
        };

        foreach (var muda in invalidas)
        {
            var r = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, muda: muda), emitido.Versao, _tecnicoA));
            Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
        }

        Assert.Equal(0, (await Detalhe(emitido.Id)).Revisao);
    }

    [FactBanco]
    public async Task Retificar_UmRascunhoEhRecusado_RascunhoSeEdita()
    {
        var par = await NovoParAsync();
        var dados = new DadosDoRelatorio(par.ClienteId, par.EquipamentoId, null, TipoServico.Corretiva, new DateOnly(2026, 10, 30), "x", null, null, null, null, 0, 10m, 0m, null);
        var rascunho = (await Servico(s => s.CriarRascunhoAsync(dados, _tecnicoA))).Valor!;

        var r = await Servico(s => s.RetificarAsync(rascunho.Id, Retificacao(rascunho, muda: d => d with { Total = 20m }), rascunho.Versao, _tecnicoA));

        Assert.Equal(CodigosDeErro.RelatorioNaoEmitido, Codigo(r));
    }

    [FactBanco]
    public async Task Retificar_UmCanceladoEhRecusado()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        var cancelado = (await Servico(s => s.CancelarAsync(emitido.Id, "Emitido por engano", emitido.Versao, _tecnicoA))).Valor!;

        var r = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(cancelado, muda: d => d with { Total = 1m }), cancelado.Versao, _gestor));

        Assert.Equal(CodigosDeErro.RelatorioNaoEmitido, Codigo(r));
    }

    [FactBanco]
    public async Task Retificar_VersaoVelhaDa409_EMalformadaEhValidacao()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        var primeira = (await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, muda: d => d with { Total = 111m }), emitido.Versao, _tecnicoA))).Valor!;

        var velha = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(primeira, muda: d => d with { Total = 222m }), emitido.Versao, _tecnicoA));
        var malformada = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(primeira, muda: d => d with { Total = 222m }), "isto não é base64", _tecnicoA));

        Assert.Equal(CodigosDeErro.Conflito, Codigo(velha));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(malformada));
        Assert.Equal(111m, (await Detalhe(emitido.Id)).Total);
    }

    [FactBanco]
    public async Task Retificar_TecnicoSoOsSeus_GestorQualquer()
    {
        var doA = await EmitidoAsync(_tecnicoA);

        var tecnicoB = await Servico(s => s.RetificarAsync(doA.Id, Retificacao(doA, muda: d => d with { Total = 500m }), doA.Versao, _tecnicoB));
        var gestor = await Servico(s => s.RetificarAsync(doA.Id, Retificacao(doA, muda: d => d with { Total = 500m }), doA.Versao, _gestor));

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(tecnicoB));
        Assert.True(gestor.Sucesso);
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.ListarRevisoesAsync(doA.Id, _tecnicoB))));
    }

    [FactBanco]
    public async Task Retificar_DuasRetificacoesSimultaneasComAMesmaVersao_UmaVence()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, muda: d => d with { Total = 200m + i }), emitido.Versao, _tecnicoA)))));

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        Assert.All(resultados.Where(r => !r.Sucesso), r => Assert.Equal(CodigosDeErro.Conflito, Codigo(r)));
        Assert.Equal(1, (await Detalhe(emitido.Id)).Revisao);
        Assert.Single((await Servico(s => s.ListarRevisoesAsync(emitido.Id, _gestor))).Valor!);
    }

    [FactBanco]
    public async Task Retificar_AtualizandoOsDadosDeCadastro_RefazOSnapshotDoClienteEDoEquipamento_NuncaODoEmissor()
    {
        var par = await NovoParAsync();
        var emitido = await EmitidoAsync(_tecnicoA, par);
        var nomeAntigo = emitido.Cliente!.Nome;

        await using (var escopo = _sp.CreateAsyncScope())
        {
            var clientes = escopo.ServiceProvider.GetRequiredService<IClienteService>();
            var atual = (await clientes.ObterAsync(par.ClienteId, true)).Valor!;
            var novo = new DadosDoCliente("Razão Social Corrigida", null, atual.Documento, null, null, null, null, null, null, null, null, null);
            Assert.True((await clientes.AtualizarAsync(par.ClienteId, novo, atual.Versao, _gestor.Id, true)).Sucesso);
        }

        // sem o pedido explícito, o snapshot continua o do momento da emissão (e a retificação, sem diferença, é recusada)
        Assert.Equal(CodigosDeErro.RetificacaoSemAlteracao, Codigo(await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido), emitido.Versao, _tecnicoA))));
        Assert.Equal(nomeAntigo, (await Detalhe(emitido.Id)).Cliente!.Nome);

        var r = await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, "Corrigir dado cadastral", d => d with { AtualizarDadosDeCadastro = true }), emitido.Versao, _tecnicoA));

        Assert.True(r.Sucesso, string.Join(";", r.Erros.Select(e => e.Mensagem)));
        Assert.Equal("Razão Social Corrigida", r.Valor!.Cliente!.Nome);
        Assert.Equal("Razão Social Corrigida", r.Valor.ClienteNome);
        Assert.Equal(emitido.Emissor, r.Valor.Emissor);   // o emissor nunca é refeito
        var revisao = Assert.Single((await Servico(s => s.ListarRevisoesAsync(emitido.Id, _gestor))).Valor!);
        Assert.Equal(["cliente"], revisao.DadosNovos.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(nomeAntigo, revisao.DadosAnteriores.GetProperty("cliente").GetProperty("nome").GetString());
        Assert.Equal("Razão Social Corrigida", revisao.DadosNovos.GetProperty("cliente").GetProperty("nome").GetString());
    }

    [FactBanco]
    public async Task Retificar_NaoMudaOClienteOEquipamentoNemOExecutor()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, muda: d => d with { Total = 300m }), emitido.Versao, _gestor));

        var depois = await Detalhe(emitido.Id);
        Assert.Equal(emitido.ClienteId, depois.ClienteId);
        Assert.Equal(emitido.EquipamentoId, depois.EquipamentoId);
        Assert.Equal(emitido.TecnicoId, depois.TecnicoId);
        Assert.Equal(emitido.EmitidoPor, depois.EmitidoPor);
        Assert.Equal(emitido.EmitidoEm, depois.EmitidoEm);
    }

    // ---------- cancelamento ----------

    [FactBanco]
    public async Task Cancelar_GravaMotivoQuemEQuando_MantemONumero_ERegistraEventoSemOMotivo()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        _relogio.Avancar(TimeSpan.FromMinutes(3));

        var r = await Servico(s => s.CancelarAsync(emitido.Id, "MOTIVO-RESERVADO do cancelamento", emitido.Versao, _tecnicoA));

        Assert.True(r.Sucesso, string.Join(";", r.Erros.Select(e => e.Mensagem)));
        var cancelado = r.Valor!;
        Assert.Equal(StatusRelatorio.Cancelado, cancelado.Status);
        Assert.Equal(emitido.Numero, cancelado.Numero);
        Assert.Equal("MOTIVO-RESERVADO do cancelamento", cancelado.MotivoCancelamento);
        Assert.Equal(_tecnicoA.Id, cancelado.CanceladoPor);
        Assert.Equal(new DateTime(2026, 10, 31, 12, 3, 0, DateTimeKind.Utc), DateTime.SpecifyKind(cancelado.CanceladoEm!.Value, DateTimeKind.Utc));
        Assert.Equal(emitido.Revisao, cancelado.Revisao);

        var evento = await Banco(db => db.Eventos.AsNoTracking().SingleAsync(e => e.RelatorioId == emitido.Id && e.Tipo == TipoEventoRelatorio.Cancelado));
        Assert.DoesNotContain("RESERVADO", evento.Detalhe);
    }

    [FactBanco]
    public async Task Cancelar_OnumeroNuncaEReutilizado_AProximaEmissaoSegue()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        await Servico(s => s.CancelarAsync(emitido.Id, "Erro de digitação", emitido.Versao, _tecnicoA));

        var proximo = await EmitidoAsync(_tecnicoA);

        Assert.Equal(emitido.Numero + 1, proximo.Numero);
        Assert.Equal(emitido.Numero, (await Detalhe(emitido.Id)).Numero);   // o cancelado continua com o seu número
    }

    [FactBanco]
    public async Task Cancelar_MotivoObrigatorio_AteQuinhentosCaracteres()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        foreach (var motivo in new string?[] { null, "", "  ", new string('m', 501) })
        {
            Assert.Equal(CodigosDeErro.Validacao, Codigo(await Servico(s => s.CancelarAsync(emitido.Id, motivo, emitido.Versao, _tecnicoA))));
        }

        Assert.Equal(StatusRelatorio.Emitido, (await Detalhe(emitido.Id)).Status);
        Assert.True((await Servico(s => s.CancelarAsync(emitido.Id, new string('m', 500), emitido.Versao, _tecnicoA))).Sucesso);
    }

    [FactBanco]
    public async Task Cancelar_SoEmitido_RascunhoECanceladoSaoRecusados()
    {
        var par = await NovoParAsync();
        var dados = new DadosDoRelatorio(par.ClienteId, par.EquipamentoId, null, TipoServico.Corretiva, new DateOnly(2026, 10, 30), "x", null, null, null, null, 0, 10m, 0m, null);
        var rascunho = (await Servico(s => s.CriarRascunhoAsync(dados, _tecnicoA))).Valor!;
        var emitido = await EmitidoAsync(_tecnicoA);
        var cancelado = (await Servico(s => s.CancelarAsync(emitido.Id, "Primeiro", emitido.Versao, _tecnicoA))).Valor!;

        Assert.Equal(CodigosDeErro.RelatorioNaoEmitido, Codigo(await Servico(s => s.CancelarAsync(rascunho.Id, "Motivo", rascunho.Versao, _tecnicoA))));
        Assert.Equal(CodigosDeErro.RelatorioNaoEmitido, Codigo(await Servico(s => s.CancelarAsync(emitido.Id, "De novo", cancelado.Versao, _tecnicoA))));
        Assert.Equal("Primeiro", (await Detalhe(emitido.Id)).MotivoCancelamento);
    }

    [FactBanco]
    public async Task Cancelar_VersaoVelhaDa409_TecnicoSoOsSeus_GestorQualquer()
    {
        var doA = await EmitidoAsync(_tecnicoA);
        var editado = (await Servico(s => s.RetificarAsync(doA.Id, Retificacao(doA, muda: d => d with { Total = 55m }), doA.Versao, _tecnicoA))).Valor!;

        Assert.Equal(CodigosDeErro.Conflito, Codigo(await Servico(s => s.CancelarAsync(doA.Id, "Motivo", doA.Versao, _tecnicoA))));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.CancelarAsync(doA.Id, "Motivo", editado.Versao, _tecnicoB))));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(await Servico(s => s.CancelarAsync(doA.Id, "Motivo", "###", _tecnicoA))));
        Assert.True((await Servico(s => s.CancelarAsync(doA.Id, "Motivo", editado.Versao, _gestor))).Sucesso);
    }

    [FactBanco]
    public async Task Cancelar_DoisCancelamentosSimultaneos_UmVence()
    {
        var emitido = await EmitidoAsync(_tecnicoA);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            Servico(s => s.CancelarAsync(emitido.Id, "Motivo", emitido.Versao, _tecnicoA)))));

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        Assert.All(resultados.Where(r => !r.Sucesso), r => Assert.Contains(Codigo(r), new[] { CodigosDeErro.Conflito, CodigosDeErro.RelatorioNaoEmitido }));
    }

    [FactBanco]
    public async Task Cancelado_ApareceNaListagemComStatusCancelado_EContinuaConsultavel()
    {
        var par = await NovoParAsync();
        var emitido = await EmitidoAsync(_tecnicoA, par);
        await Servico(s => s.CancelarAsync(emitido.Id, "Cancelado", emitido.Versao, _tecnicoA));

        var lista = await Servico(s => s.ListarAsync(new FiltroDeRelatorios(par.ClienteId, null, null, StatusRelatorio.Cancelado, null, null, null, null), _tecnicoA));

        var item = Assert.Single(lista.Itens);
        Assert.Equal(emitido.Numero, item.Numero);
        Assert.Equal(StatusRelatorio.Cancelado, item.Status);
        Assert.True((await Servico(s => s.ObterAsync(emitido.Id, _tecnicoA))).Sucesso);
    }

    // ---------- integridade no banco ----------

    [FactBanco]
    public async Task Banco_RecusaCanceladoSemMotivo_EmitidoComDadosDeCancelamento_GarantiaAcimaDoTeto_ERevisaoRepetida()
    {
        var emitido = await EmitidoAsync(_tecnicoA);
        var retificado = (await Servico(s => s.RetificarAsync(emitido.Id, Retificacao(emitido, muda: d => d with { Total = 77m }), emitido.Versao, _tecnicoA))).Valor!;

        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            var r = await db.Relatorios.SingleAsync(x => x.Id == emitido.Id);
            r.Status = StatusRelatorio.Cancelado;                       // cancelado sem quando/quem/motivo
            return await db.SaveChangesAsync();
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            var r = await db.Relatorios.SingleAsync(x => x.Id == emitido.Id);
            r.MotivoCancelamento = "motivo solto";                      // emitido com dado de cancelamento
            return await db.SaveChangesAsync();
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            var r = await db.Relatorios.SingleAsync(x => x.Id == emitido.Id);
            r.GarantiaDias = 10000;                                     // acima do teto de 9.999
            return await db.SaveChangesAsync();
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(async db =>
        {
            db.Revisoes.Add(new RelatorioRevisao                        // revisão 1 já existe para este relatório
            {
                Id = Guid.NewGuid(), RelatorioId = emitido.Id, Revisao = retificado.Revisao, RetificadoEm = DateTime.UtcNow,
                RetificadoPor = _tecnicoA.Id, Motivo = "duplicada"
            });
            return await db.SaveChangesAsync();
        }));
    }

    [FactBanco]
    public async Task Listagem_ComAProjecaoEnxuta_ContinuaMostrandoNumeroNomesDoSnapshotETotal()
    {
        var par = await NovoParAsync();
        var emitido = await EmitidoAsync(_tecnicoA, par);

        var item = Assert.Single((await Servico(s => s.ListarAsync(new FiltroDeRelatorios(par.ClienteId, null, null, null, null, null, null, null), _gestor))).Itens);

        Assert.Equal(emitido.Numero, item.Numero);
        Assert.Equal(emitido.Cliente!.Nome, item.ClienteNome);
        Assert.Equal(emitido.Equipamento!.Descricao, item.EquipamentoDescricao);
        Assert.Equal(emitido.TotalGeral, item.TotalGeral);
        Assert.Equal(emitido.TecnicoNome, item.TecnicoNome);
        Assert.Equal(emitido.Revisao, item.Revisao);
    }
}
