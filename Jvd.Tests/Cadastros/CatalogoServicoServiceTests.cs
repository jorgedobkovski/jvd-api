using Jvd.Application.Catalogo;
using Jvd.Application.Comum;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Cadastros;

/// <summary>Regras do catálogo de serviços sobre SQL Server real (banco descartável). Dados SINTÉTICOS. Só com JVD_TESTES_BANCO=1.</summary>
public class CatalogoServicoServiceTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _sp = null!;
    private Guid _ator;

    public CatalogoServicoServiceTests(BancoDeTeste banco) => _banco = banco;

    public async Task InitializeAsync()
    {
        _sp = ServicosDeTeste.Construir(_banco.Conexao, _relogio);
        _ator = (await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor)).Id;
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

    // ---------- apoio ----------

    private async Task<T> Servico<T>(Func<ICatalogoServicoService, Task<T>> acao)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<ICatalogoServicoService>());
    }

    private static DadosDoServico Dados(string? descricao = null, decimal? preco = null, string? codigo = null) =>
        new(descricao ?? "Serviço sintético " + Guid.NewGuid().ToString("N")[..8], codigo, preco);

    private async Task<ServicoResumo> CriarAsync(DadosDoServico? dados = null)
    {
        var r = await Servico(s => s.CriarAsync(dados ?? Dados(), _ator));
        Assert.True(r.Sucesso);
        return r.Valor!.Servico;
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
    public async Task Criar_SoComDescricao_PrecoNulo_Ativo()
    {
        var s = await CriarAsync(Dados("Só descrição"));

        Assert.Null(s.PrecoSugerido);
        Assert.Null(s.CodigoOrigem);
        Assert.True(s.Ativo);
        Assert.False(string.IsNullOrEmpty(s.Versao));
    }

    [FactBanco]
    public async Task Criar_GuardaPrecoComDuasCasas_ECodigoAparado()
    {
        var s = await CriarAsync(Dados(preco: 1234.56m, codigo: "  C-01  "));

        var lido = (await Servico(x => x.ObterAsync(s.Id, true))).Valor!;
        Assert.Equal(1234.56m, lido.PrecoSugerido);
        Assert.Equal("C-01", lido.CodigoOrigem);
    }

    [FactBanco]
    public async Task Criar_PrecoZeroEhValido()
    {
        var s = await CriarAsync(Dados(preco: 0m));

        Assert.Equal(0m, s.PrecoSugerido);
    }

    [FactBanco]
    public async Task Criar_PrecosInvalidos_SaoRecusados_SemArredondar()
    {
        foreach (var preco in new[] { -0.01m, 1.005m, 10_000_000_000.00m })
        {
            var r = await Servico(s => s.CriarAsync(Dados(preco: preco), _ator));
            Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
        }

        var maximo = await Servico(s => s.CriarAsync(Dados(preco: 9_999_999_999.99m), _ator));
        Assert.True(maximo.Sucesso);
    }

    [FactBanco]
    public async Task Criar_DadosInvalidos_ListaErros_SemEcoarValores()
    {
        var r = await Servico(s => s.CriarAsync(new DadosDoServico("  ", new string('c', 41), -5m), _ator));

        Assert.False(r.Sucesso);
        Assert.Equal(3, r.Erros.Count);
        Assert.All(r.Erros, e => Assert.Equal(CodigosDeErro.Validacao, e.Codigo));
    }

    [FactBanco]
    public async Task Criar_DescricaoRepetida_AvisaMasNaoBloqueia()
    {
        var descricao = "Troca de teclado " + Guid.NewGuid().ToString("N")[..8];
        var primeiro = await CriarAsync(Dados(descricao));

        var r = await Servico(s => s.CriarAsync(Dados(descricao.ToUpperInvariant()), _ator));

        Assert.True(r.Sucesso);
        var aviso = Assert.Single(r.Valor!.Avisos);
        Assert.Equal("catalogo.descricao_repetida", aviso.Codigo);
        Assert.Contains(primeiro.Id, aviso.Ids);
    }

    [FactBanco]
    public async Task Criar_RegistraEvento_SoComNomesDeCampos()
    {
        var s = await CriarAsync(Dados("Descrição Reservada", 99.90m));

        var evento = Assert.Single(await EventosAsync(s.Id));
        Assert.Equal("catalogo_servico", evento.Entidade);
        Assert.Equal(TipoEventoCadastro.Criado, evento.Tipo);
        Assert.Equal("descricao,preco_sugerido", evento.Campos);
        Assert.DoesNotContain("Reservada", evento.Campos);
    }

    // ---------- edição e concorrência ----------

    [FactBanco]
    public async Task Atualizar_MudaCampos_NovaVersao_ERegistraSoNomes()
    {
        var s = await CriarAsync(Dados("Antes", 10m));
        _relogio.Avancar(TimeSpan.FromMinutes(5));

        var r = await Servico(x => x.AtualizarAsync(s.Id, Dados("Depois", 12m), s.Versao, _ator));

        Assert.True(r.Sucesso);
        Assert.Equal("Depois", r.Valor!.Servico.Descricao);
        Assert.NotEqual(s.Versao, r.Valor.Servico.Versao);
        var editado = (await EventosAsync(s.Id)).Single(e => e.Tipo == TipoEventoCadastro.Editado);
        Assert.Equal("descricao,preco_sugerido", editado.Campos);
    }

    [FactBanco]
    public async Task Atualizar_LimparOPreco_ContaComoAlteracao()
    {
        var s = await CriarAsync(Dados("Com preço", 10m));

        var r = await Servico(x => x.AtualizarAsync(s.Id, Dados("Com preço", null), s.Versao, _ator));

        Assert.True(r.Sucesso);
        Assert.Null(r.Valor!.Servico.PrecoSugerido);
    }

    [FactBanco]
    public async Task Atualizar_SemMudancas_NaoGeraEventoNemNovaVersao()
    {
        var dados = Dados("Igual", 5m);
        var s = await CriarAsync(dados);

        var r = await Servico(x => x.AtualizarAsync(s.Id, dados, s.Versao, _ator));

        Assert.True(r.Sucesso);
        Assert.Equal(s.Versao, r.Valor!.Servico.Versao);
        Assert.Single(await EventosAsync(s.Id));
    }

    [FactBanco]
    public async Task Atualizar_VersaoVelha_Retorna409_ENaoSobrescreve()
    {
        var s = await CriarAsync(Dados("Original"));
        Assert.True((await Servico(x => x.AtualizarAsync(s.Id, Dados("Primeira"), s.Versao, _ator))).Sucesso);

        var r = await Servico(x => x.AtualizarAsync(s.Id, Dados("Segunda"), s.Versao, _ator));

        Assert.Equal(CodigosDeErro.Conflito, Codigo(r));
        Assert.Equal("Primeira", (await Servico(x => x.ObterAsync(s.Id, true))).Valor!.Descricao);
    }

    [FactBanco]
    public async Task Atualizar_VersaoMalformada_EhValidacao_EInexistenteEh404()
    {
        var s = await CriarAsync();

        Assert.Equal(CodigosDeErro.Validacao, Codigo(await Servico(x => x.AtualizarAsync(s.Id, Dados(), "###", _ator))));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(x => x.AtualizarAsync(Guid.NewGuid(), Dados(), "AAAAAAAAB9E=", _ator))));
    }

    [FactBanco]
    public async Task Atualizar_ParaDescricaoExistente_Avisa()
    {
        var descricao = "Limpeza " + Guid.NewGuid().ToString("N")[..8];
        var existente = await CriarAsync(Dados(descricao));
        var outro = await CriarAsync(Dados());

        var r = await Servico(x => x.AtualizarAsync(outro.Id, Dados(descricao), outro.Versao, _ator));

        Assert.True(r.Sucesso);
        Assert.Contains(existente.Id, Assert.Single(r.Valor!.Avisos).Ids);
    }

    [FactBanco]
    public async Task Atualizar_DuasEdicoesSimultaneasComAMesmaVersao_SoUmaVence()
    {
        var s = await CriarAsync(Dados("Base"));

        var tarefas = Enumerable.Range(0, 2)
            .Select(i => Task.Run(() => Servico(x => x.AtualizarAsync(s.Id, Dados("Edicao " + i), s.Versao, _ator))))
            .ToArray();
        var resultados = await Task.WhenAll(tarefas);

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        Assert.Equal(1, resultados.Count(r => !r.Sucesso && Codigo(r) == CodigosDeErro.Conflito));
    }

    // ---------- desativar / visibilidade ----------

    [FactBanco]
    public async Task DesativarEReativar_SaoIdempotentes_ERegistramEventoUmaVezCada()
    {
        var s = await CriarAsync();

        Assert.True((await Servico(x => x.DesativarAsync(s.Id, _ator))).Sucesso);
        Assert.True((await Servico(x => x.DesativarAsync(s.Id, _ator))).Sucesso);
        Assert.True((await Servico(x => x.ReativarAsync(s.Id, _ator))).Sucesso);
        Assert.True((await Servico(x => x.ReativarAsync(s.Id, _ator))).Sucesso);

        var tipos = (await EventosAsync(s.Id)).Select(e => e.Tipo).ToList();
        Assert.Equal(1, tipos.Count(t => t == TipoEventoCadastro.Desativado));
        Assert.Equal(1, tipos.Count(t => t == TipoEventoCadastro.Reativado));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(x => x.DesativarAsync(Guid.NewGuid(), _ator))));
    }

    [FactBanco]
    public async Task Inativo_ESoVisivelParaQuemPodeVerInativos_MesmoPedindoOFiltroAtivoFalso()
    {
        var marca = "Inat" + Guid.NewGuid().ToString("N")[..6];
        var s = await CriarAsync(Dados(marca));
        await Servico(x => x.DesativarAsync(s.Id, _ator));

        Assert.False((await Servico(x => x.ObterAsync(s.Id, incluirInativos: false))).Sucesso);
        Assert.True((await Servico(x => x.ObterAsync(s.Id, incluirInativos: true))).Sucesso);

        var tecnico = await Servico(x => x.ListarAsync(new FiltroDeServicos(marca, Ativo: false), incluirInativos: false));
        Assert.Empty(tecnico.Itens);
        Assert.Equal(0, tecnico.Total);

        var gestor = await Servico(x => x.ListarAsync(new FiltroDeServicos(marca, Ativo: false), incluirInativos: true));
        Assert.Single(gestor.Itens);
        var gestorAtivos = await Servico(x => x.ListarAsync(new FiltroDeServicos(marca, Ativo: true), incluirInativos: true));
        Assert.Empty(gestorAtivos.Itens);
    }

    // ---------- listagem ----------

    [FactBanco]
    public async Task Listar_BuscaPorDescricaoECodigo_OrdenaPorDescricao_EPagina()
    {
        var marca = "Ord" + Guid.NewGuid().ToString("N")[..6];
        await CriarAsync(Dados($"{marca} C"));
        await CriarAsync(Dados($"{marca} A", codigo: "COD-" + marca));
        await CriarAsync(Dados($"{marca} B"));

        var todos = await Servico(x => x.ListarAsync(new FiltroDeServicos(marca, null), true));
        Assert.Equal([$"{marca} A", $"{marca} B", $"{marca} C"], todos.Itens.Select(i => i.Descricao).ToArray());

        var porCodigo = await Servico(x => x.ListarAsync(new FiltroDeServicos("COD-" + marca, null), true));
        Assert.Single(porCodigo.Itens);

        var pagina2 = await Servico(x => x.ListarAsync(new FiltroDeServicos(marca, null, Pagina: 2, Tamanho: 2), true));
        Assert.Equal(3, pagina2.Total);
        Assert.Single(pagina2.Itens);
    }

    [FactBanco]
    public async Task Listar_CuringasDoLikeSaoTratadosComoTextoLiteral_ELimitaPaginaACem()
    {
        await CriarAsync(Dados("Serviço comum"));

        foreach (var busca in new[] { "%", "_", "[a-z]" })
        {
            var pagina = await Servico(x => x.ListarAsync(new FiltroDeServicos(busca, null), true));
            Assert.Empty(pagina.Itens);
        }

        var grande = await Servico(x => x.ListarAsync(new FiltroDeServicos(null, null, Pagina: -3, Tamanho: 100000), true));
        Assert.Equal(1, grande.Pagina);
        Assert.Equal(100, grande.Tamanho);
    }
}
