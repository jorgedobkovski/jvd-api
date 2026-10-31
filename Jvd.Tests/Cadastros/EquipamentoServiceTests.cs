using Jvd.Application.Clientes;
using Jvd.Application.Comum;
using Jvd.Application.Equipamentos;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Cadastros;

/// <summary>Regras de cadastro de equipamentos sobre SQL Server real (banco descartável). Dados SINTÉTICOS. Só com JVD_TESTES_BANCO=1.</summary>
public class EquipamentoServiceTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _sp = null!;
    private Guid _ator;

    public EquipamentoServiceTests(BancoDeTeste banco) => _banco = banco;

    public async Task InitializeAsync()
    {
        _sp = ServicosDeTeste.Construir(_banco.Conexao, _relogio);
        _ator = (await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor)).Id;
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

    // ---------- apoio ----------

    private async Task<T> Equip<T>(Func<IEquipamentoService, Task<T>> acao)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IEquipamentoService>());
    }

    private async Task<T> Cli<T>(Func<IClienteService, Task<T>> acao)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IClienteService>());
    }

    private async Task<ClienteResumo> NovoClienteAsync()
    {
        var r = await Cli(s => s.CriarAsync(ClienteServiceTests.Dados(), _ator));
        Assert.True(r.Sucesso);
        return r.Valor!.Cliente;
    }

    private static DadosDoEquipamento Dados(Guid clienteId, string descricao = "Balança sintética", string? serie = null, string? inmetro = null) =>
        new(clienteId, descricao, serie, inmetro, null);

    private async Task<EquipamentoResumo> NovoEquipamentoAsync(Guid clienteId, DadosDoEquipamento? dados = null)
    {
        var r = await Equip(s => s.CriarAsync(dados ?? Dados(clienteId), _ator));
        Assert.True(r.Sucesso);
        return r.Valor!.Equipamento;
    }

    private async Task<List<EventoDeCadastro>> EventosAsync(Guid id)
    {
        await using var escopo = _sp.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        return await db.EventosDeCadastro.AsNoTracking().Where(e => e.EntidadeId == id).OrderBy(e => e.OcorridoEm).ToListAsync();
    }

    private static string? Codigo(Resultado r) => r.Erros.FirstOrDefault()?.Codigo;

    // ---------- criação ----------

    [FactBanco]
    public async Task Criar_VinculaAoClienteEDevolveONome()
    {
        var cliente = await NovoClienteAsync();

        var e = await NovoEquipamentoAsync(cliente.Id, Dados(cliente.Id, serie: "S-1", inmetro: "I-1"));

        Assert.Equal(cliente.Id, e.ClienteId);
        Assert.Equal(cliente.NomeRazaoSocial, e.ClienteNome);
        Assert.True(e.Ativo);
        Assert.False(string.IsNullOrEmpty(e.Versao));
    }

    [FactBanco]
    public async Task Criar_ClienteInexistente_OuInativo_EhRecusado()
    {
        var inexistente = await Equip(s => s.CriarAsync(Dados(Guid.NewGuid()), _ator));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(inexistente));

        var cliente = await NovoClienteAsync();
        await Cli(s => s.DesativarAsync(cliente.Id, _ator));
        var inativo = await Equip(s => s.CriarAsync(Dados(cliente.Id), _ator));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(inativo));
    }

    [FactBanco]
    public async Task Criar_DadosInvalidos_ListaErros()
    {
        var cliente = await NovoClienteAsync();
        var dados = new DadosDoEquipamento(cliente.Id, " ", new string('s', 61), new string('i', 41), new string('o', 1001));

        var r = await Equip(s => s.CriarAsync(dados, _ator));

        Assert.False(r.Sucesso);
        Assert.Equal(4, r.Erros.Count);
        Assert.All(r.Erros, e => Assert.Equal(CodigosDeErro.Validacao, e.Codigo));
    }

    [FactBanco]
    public async Task Criar_SerieEInmetroRepetidos_AvisamMasNaoBloqueiam()
    {
        var cliente = await NovoClienteAsync();
        var serie = "SER-" + Guid.NewGuid().ToString("N")[..8];
        var inmetro = "INM-" + Guid.NewGuid().ToString("N")[..8];
        var primeiro = await NovoEquipamentoAsync(cliente.Id, Dados(cliente.Id, serie: serie, inmetro: inmetro));

        var r = await Equip(s => s.CriarAsync(Dados(cliente.Id, serie: serie, inmetro: inmetro), _ator));

        Assert.True(r.Sucesso);
        Assert.Equal(["equipamento.inmetro_repetido", "equipamento.serie_repetida"], r.Valor!.Avisos.Select(a => a.Codigo).Order().ToArray());
        Assert.All(r.Valor.Avisos, a => Assert.Contains(primeiro.Id, a.Ids));
    }

    [FactBanco]
    public async Task Criar_RegistraEvento_SoComNomesDeCampos()
    {
        var cliente = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(cliente.Id, Dados(cliente.Id, "Descricao Reservada", serie: "SERIE-RESERVADA"));

        var evento = Assert.Single(await EventosAsync(e.Id));
        Assert.Equal("equipamento", evento.Entidade);
        Assert.Equal("cliente,descricao,numero_serie", evento.Campos);
        Assert.DoesNotContain("RESERVADA", evento.Campos, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- edição, transferência e concorrência ----------

    [FactBanco]
    public async Task Atualizar_VersaoVelha_Retorna409()
    {
        var cliente = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(cliente.Id);
        Assert.True((await Equip(s => s.AtualizarAsync(e.Id, Dados(cliente.Id, "Primeira"), e.Versao, _ator, false))).Sucesso);

        var r = await Equip(s => s.AtualizarAsync(e.Id, Dados(cliente.Id, "Segunda"), e.Versao, _ator, false));

        Assert.Equal(CodigosDeErro.Conflito, Codigo(r));
    }

    [FactBanco]
    public async Task Atualizar_TransfereParaOutroCliente_ERegistraCampoCliente()
    {
        var origem = await NovoClienteAsync();
        var destino = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(origem.Id);

        var r = await Equip(s => s.AtualizarAsync(e.Id, Dados(destino.Id), e.Versao, _ator, false));

        Assert.True(r.Sucesso);
        Assert.Equal(destino.Id, r.Valor!.Equipamento.ClienteId);
        Assert.Equal(destino.NomeRazaoSocial, r.Valor.Equipamento.ClienteNome);
        var editado = (await EventosAsync(e.Id)).Single(x => x.Tipo == TipoEventoCadastro.Editado);
        Assert.Equal("cliente", editado.Campos);
    }

    [FactBanco]
    public async Task Atualizar_TransferirParaClienteInativoOuInexistente_EhRecusado()
    {
        var origem = await NovoClienteAsync();
        var destino = await NovoClienteAsync();
        await Cli(s => s.DesativarAsync(destino.Id, _ator));
        var e = await NovoEquipamentoAsync(origem.Id);

        var inativo = await Equip(s => s.AtualizarAsync(e.Id, Dados(destino.Id), e.Versao, _ator, false));
        var inexistente = await Equip(s => s.AtualizarAsync(e.Id, Dados(Guid.NewGuid()), e.Versao, _ator, false));

        Assert.Equal(CodigosDeErro.Validacao, Codigo(inativo));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(inexistente));
    }

    [FactBanco]
    public async Task Atualizar_MantendoClienteQueFoiDesativado_AindaEhPermitido()
    {
        var cliente = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(cliente.Id);
        await Cli(s => s.DesativarAsync(cliente.Id, _ator));

        var r = await Equip(s => s.AtualizarAsync(e.Id, Dados(cliente.Id, "Descricao nova"), e.Versao, _ator, podeEditarInativo: true));

        Assert.True(r.Sucesso);
    }

    [FactBanco]
    public async Task Atualizar_SemMudancas_NaoGeraEvento()
    {
        var cliente = await NovoClienteAsync();
        var dados = Dados(cliente.Id, "Igual", "S", "I");
        var e = await NovoEquipamentoAsync(cliente.Id, dados);

        var r = await Equip(s => s.AtualizarAsync(e.Id, dados, e.Versao, _ator, false));

        Assert.True(r.Sucesso);
        Assert.Equal(e.Versao, r.Valor!.Equipamento.Versao);
        Assert.Single(await EventosAsync(e.Id));
    }

    [FactBanco]
    public async Task Atualizar_VersaoMalFormada_EhValidacao()
    {
        var cliente = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(cliente.Id);

        var r = await Equip(s => s.AtualizarAsync(e.Id, Dados(cliente.Id), "###", _ator, false));

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    // ---------- visibilidade ----------

    [FactBanco]
    public async Task Tecnico_SoVeEquipamentoAtivoDeClienteAtivo()
    {
        var cliente = await NovoClienteAsync();
        var ativo = await NovoEquipamentoAsync(cliente.Id, Dados(cliente.Id, "Ativo Visivel"));
        var inativo = await NovoEquipamentoAsync(cliente.Id, Dados(cliente.Id, "Equipamento Inativo"));
        await Equip(s => s.DesativarAsync(inativo.Id, _ator));

        var comoTecnico = await Equip(s => s.ListarAsync(new FiltroDeEquipamentos(cliente.Id, null, Ativo: false), incluirInativos: false));
        Assert.Equal([ativo.Id], comoTecnico.Itens.Select(i => i.Id).ToArray());
        Assert.False((await Equip(s => s.ObterAsync(inativo.Id, false))).Sucesso);

        await Cli(s => s.DesativarAsync(cliente.Id, _ator));
        var clienteInativo = await Equip(s => s.ListarAsync(new FiltroDeEquipamentos(cliente.Id, null, null), incluirInativos: false));
        Assert.Empty(clienteInativo.Itens);
        Assert.False((await Equip(s => s.ObterAsync(ativo.Id, false))).Sucesso);

        var comoGestor = await Equip(s => s.ListarAsync(new FiltroDeEquipamentos(cliente.Id, null, null), incluirInativos: true));
        Assert.Equal(2, comoGestor.Total);
    }

    [FactBanco]
    public async Task Atualizar_EquipamentoInativo_SoOGestorEdita()
    {
        var cliente = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(cliente.Id);
        await Equip(s => s.DesativarAsync(e.Id, _ator));

        var tecnico = await Equip(s => s.AtualizarAsync(e.Id, Dados(cliente.Id, "Nova"), e.Versao, _ator, podeEditarInativo: false));

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(tecnico));
    }

    [FactBanco]
    public async Task Tecnico_NaoEditaNemTransfereEquipamentoDeClienteInativo()
    {
        var origem = await NovoClienteAsync();
        var destino = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(origem.Id);
        await Cli(s => s.DesativarAsync(origem.Id, _ator));

        var edicao = await Equip(s => s.AtualizarAsync(e.Id, Dados(origem.Id, "Nova"), e.Versao, _ator, podeEditarInativo: false));
        var transferencia = await Equip(s => s.AtualizarAsync(e.Id, Dados(destino.Id), e.Versao, _ator, podeEditarInativo: false));

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(edicao));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(transferencia));
        var intacto = (await Equip(s => s.ObterAsync(e.Id, incluirInativos: true))).Valor!;
        Assert.Equal(origem.Id, intacto.ClienteId);
        Assert.Equal(e.Versao, intacto.Versao);
    }

    [FactBanco]
    public async Task TransferenciaConcorrenteComDesativacaoDoDestino_NaoAceitaTransferenciaSeDestinoJaInativo()
    {
        for (var rodada = 0; rodada < 5; rodada++)
        {
            var origem = await NovoClienteAsync();
            var destino = await NovoClienteAsync();
            var e = await NovoEquipamentoAsync(origem.Id);

            var transferir = Task.Run(() => Equip(s => s.AtualizarAsync(e.Id, Dados(destino.Id), e.Versao, _ator, false)));
            var desativar = Task.Run(() => Cli(s => s.DesativarAsync(destino.Id, _ator)));
            Resultado[] resultados = [await transferir, await desativar];

            Assert.All(resultados.Where(r => !r.Sucesso), r => Assert.Contains(r.Erros[0].Codigo, new[] { CodigosDeErro.Validacao, CodigosDeErro.Conflito }));

            // Ordens válidas (serializáveis): transferir e depois desativar, ou desativar e então recusar a transferência.
            // Nunca pode haver erro inesperado, nem transferência "aceita" que não esteja gravada, nem recusa que mova o equipamento.
            var equipamento = (await Equip(s => s.ObterAsync(e.Id, incluirInativos: true))).Valor!;
            Assert.Equal(resultados[0].Sucesso ? destino.Id : origem.Id, equipamento.ClienteId);
            Assert.True(resultados[1].Sucesso);
        }
    }

    [FactBanco]
    public async Task DesativarEReativar_SaoIdempotentes()
    {
        var cliente = await NovoClienteAsync();
        var e = await NovoEquipamentoAsync(cliente.Id);

        Assert.True((await Equip(s => s.DesativarAsync(e.Id, _ator))).Sucesso);
        Assert.True((await Equip(s => s.DesativarAsync(e.Id, _ator))).Sucesso);
        Assert.True((await Equip(s => s.ReativarAsync(e.Id, _ator))).Sucesso);

        var tipos = (await EventosAsync(e.Id)).Select(x => x.Tipo).ToList();
        Assert.Equal(1, tipos.Count(t => t == TipoEventoCadastro.Desativado));
        Assert.Equal(1, tipos.Count(t => t == TipoEventoCadastro.Reativado));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Equip(s => s.DesativarAsync(Guid.NewGuid(), _ator))));
    }

    // ---------- listagem ----------

    [FactBanco]
    public async Task Listar_BuscaPorDescricaoSerieInmetroENomeDoCliente()
    {
        var marca = "Mx" + Guid.NewGuid().ToString("N")[..6];
        var cliente = (await Cli(s => s.CriarAsync(ClienteServiceTests.Dados("Cliente " + marca), _ator))).Valor!.Cliente;
        var e = await NovoEquipamentoAsync(cliente.Id, Dados(cliente.Id, "Desc " + marca, "Ser " + marca, "Inm " + marca));

        foreach (var busca in new[] { "Desc " + marca, "Ser " + marca, "Inm " + marca, "Cliente " + marca })
        {
            var pagina = await Equip(s => s.ListarAsync(new FiltroDeEquipamentos(null, busca, null), true));
            Assert.Contains(pagina.Itens, i => i.Id == e.Id);
        }
    }

    [FactBanco]
    public async Task Listar_LimitaOTamanhoDaPaginaACem()
    {
        var pagina = await Equip(s => s.ListarAsync(new FiltroDeEquipamentos(null, null, null, Pagina: 0, Tamanho: 5000), true));

        Assert.Equal(1, pagina.Pagina);
        Assert.Equal(100, pagina.Tamanho);
    }
}
