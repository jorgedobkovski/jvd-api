using Jvd.Application.Clientes;
using Jvd.Application.Comum;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Cadastros;

/// <summary>Regras de cadastro de clientes sobre SQL Server real (banco descartável). Dados SINTÉTICOS. Só com JVD_TESTES_BANCO=1.</summary>
public class ClienteServiceTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    public const string CnpjValido = "11222333000181";
    public const string CpfValido = "52998224725";

    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _sp = null!;
    private Guid _ator;

    public ClienteServiceTests(BancoDeTeste banco) => _banco = banco;

    public async Task InitializeAsync()
    {
        _sp = ServicosDeTeste.Construir(_banco.Conexao, _relogio);
        _ator = (await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor)).Id;
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

    // ---------- apoio ----------

    private async Task<T> Servico<T>(Func<IClienteService, Task<T>> acao)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IClienteService>());
    }

    public static DadosDoCliente Dados(string? nome = null, string? documento = null) =>
        new(nome ?? "Cliente Sintético " + Guid.NewGuid().ToString("N")[..8], null, documento, null, null, null, null, null, null, null, null, null);

    private async Task<ClienteResumo> CriarAsync(DadosDoCliente? dados = null)
    {
        var r = await Servico(s => s.CriarAsync(dados ?? Dados(), _ator));
        Assert.True(r.Sucesso);
        return r.Valor!.Cliente;
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
    public async Task Criar_ComSoONome_FicaIncompletoEManual()
    {
        var c = await CriarAsync(Dados("Só Nome"));

        Assert.True(c.CadastroIncompleto);
        Assert.Equal(OrigemCadastro.Manual, c.Origem);
        Assert.True(c.Ativo);
        Assert.False(string.IsNullOrEmpty(c.Versao));
    }

    [FactBanco]
    public async Task Criar_ComDocumentoValido_FicaCompleto_ENormalizaMascara()
    {
        var c = await CriarAsync(Dados(documento: "11.222.333/0001-81"));

        Assert.False(c.CadastroIncompleto);
        Assert.Equal(CnpjValido, c.Documento);
    }

    [FactBanco]
    public async Task Criar_NormalizaCepEUf()
    {
        var dados = Dados() with { Cep = "76.800-000", Uf = " ro " };

        var c = await CriarAsync(dados);

        Assert.Equal("76800000", c.Cep);
        Assert.Equal("RO", c.Uf);
    }

    [FactBanco]
    public async Task Criar_DadosInvalidos_ListaOsErros_SemEcoarValores()
    {
        var dados = new DadosDoCliente("  ", null, "123", null, null, null, null, "12", null, "ZZ", null, "isto-nao-e-email");

        var r = await Servico(s => s.CriarAsync(dados, _ator));

        Assert.False(r.Sucesso);
        Assert.All(r.Erros, e => Assert.Equal(CodigosDeErro.Validacao, e.Codigo));
        Assert.True(r.Erros.Count >= 5);
        Assert.DoesNotContain(r.Erros, e => e.Mensagem.Contains("isto-nao-e-email") || e.Mensagem.Contains("ZZ"));
    }

    [FactBanco]
    public async Task Criar_ExcedendoTamanhos_EhRecusado()
    {
        var r = await Servico(s => s.CriarAsync(Dados(nome: new string('x', 201)), _ator));

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    [FactBanco]
    public async Task Criar_DocumentoRepetido_AvisaMasNaoBloqueia()
    {
        var documento = DadosDeTeste.NovoCpf();
        var primeiro = await CriarAsync(Dados(documento: documento));

        var r = await Servico(s => s.CriarAsync(Dados(documento: documento), _ator));

        Assert.True(r.Sucesso);
        var aviso = Assert.Single(r.Valor!.Avisos);
        Assert.Equal("cliente.documento_repetido", aviso.Codigo);
        Assert.Contains(primeiro.Id, aviso.Ids);
    }

    [FactBanco]
    public async Task Criar_RegistraEvento_SoComNomesDeCampos()
    {
        var c = await CriarAsync(Dados("Nome Secreto", CpfValido));

        var evento = Assert.Single(await EventosAsync(c.Id));
        Assert.Equal("cliente", evento.Entidade);
        Assert.Equal(TipoEventoCadastro.Criado, evento.Tipo);
        Assert.Equal(_ator, evento.AtorId);
        Assert.Contains("nome_razao_social", evento.Campos);
        Assert.Contains("documento", evento.Campos);
        Assert.DoesNotContain("Nome Secreto", evento.Campos);
        Assert.DoesNotContain(CpfValido, evento.Campos);
    }

    // ---------- edição e concorrência ----------

    [FactBanco]
    public async Task Atualizar_MudaCampos_GeraNovaVersao_ERegistraSoNomes()
    {
        var c = await CriarAsync();
        _relogio.Avancar(TimeSpan.FromMinutes(5));

        var r = await Servico(s => s.AtualizarAsync(c.Id, Dados("Outro Nome", CnpjValido), c.Versao, _ator, podeEditarInativo: false));

        Assert.True(r.Sucesso);
        Assert.Equal("Outro Nome", r.Valor!.Cliente.NomeRazaoSocial);
        Assert.False(r.Valor.Cliente.CadastroIncompleto);
        Assert.NotEqual(c.Versao, r.Valor.Cliente.Versao);

        var editado = (await EventosAsync(c.Id)).Single(e => e.Tipo == TipoEventoCadastro.Editado);
        Assert.Equal("nome_razao_social,documento", editado.Campos);
    }

    [FactBanco]
    public async Task Atualizar_SemMudancas_NaoGeraEventoNemNovaVersao()
    {
        var dados = Dados("Igual", CnpjValido);
        var c = await CriarAsync(dados);

        var r = await Servico(s => s.AtualizarAsync(c.Id, dados, c.Versao, _ator, false));

        Assert.True(r.Sucesso);
        Assert.Equal(c.Versao, r.Valor!.Cliente.Versao);
        Assert.Single(await EventosAsync(c.Id));
    }

    [FactBanco]
    public async Task Atualizar_ComVersaoVelha_Retorna409Conflito_ENaoSobrescreve()
    {
        var c = await CriarAsync(Dados("Original"));
        var primeira = await Servico(s => s.AtualizarAsync(c.Id, Dados("Primeira Edicao"), c.Versao, _ator, false));
        Assert.True(primeira.Sucesso);

        var segunda = await Servico(s => s.AtualizarAsync(c.Id, Dados("Segunda Edicao"), c.Versao, _ator, false));

        Assert.False(segunda.Sucesso);
        Assert.Equal(CodigosDeErro.Conflito, Codigo(segunda));
        var atual = await Servico(s => s.ObterAsync(c.Id, true));
        Assert.Equal("Primeira Edicao", atual.Valor!.NomeRazaoSocial);
    }

    [FactBanco]
    public async Task Atualizar_DuasEdicoesSimultaneasComAMesmaVersao_SoUmaVence()
    {
        var c = await CriarAsync(Dados("Base"));

        var tarefas = Enumerable.Range(0, 2)
            .Select(i => Task.Run(() => Servico(s => s.AtualizarAsync(c.Id, Dados("Edicao " + i), c.Versao, _ator, false))))
            .ToArray();
        var resultados = await Task.WhenAll(tarefas);

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        Assert.Equal(1, resultados.Count(r => !r.Sucesso && Codigo(r) == CodigosDeErro.Conflito));
    }

    [FactBanco]
    public async Task Atualizar_VersaoMalFormada_EhValidacao()
    {
        var c = await CriarAsync();

        var r = await Servico(s => s.AtualizarAsync(c.Id, Dados(), "isto não é base64", _ator, false));

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    [FactBanco]
    public async Task Atualizar_MudandoODocumentoParaUmExistente_Avisa()
    {
        var documento = DadosDeTeste.NovoCpf();
        var existente = await CriarAsync(Dados(documento: documento));
        var outro = await CriarAsync(Dados());

        var r = await Servico(s => s.AtualizarAsync(outro.Id, Dados("Novo Nome", documento), outro.Versao, _ator, false));

        Assert.True(r.Sucesso);
        Assert.Contains(existente.Id, Assert.Single(r.Valor!.Avisos).Ids);
    }

    [FactBanco]
    public async Task Atualizar_Inexistente_Retorna404()
    {
        var r = await Servico(s => s.AtualizarAsync(Guid.NewGuid(), Dados(), "AAAAAAAAB9E=", _ator, true));

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(r));
    }

    // ---------- desativar / reativar / visibilidade ----------

    [FactBanco]
    public async Task DesativarEReativar_SaoIdempotentes_ERegistramEventoUmaVezCada()
    {
        var c = await CriarAsync();

        Assert.True((await Servico(s => s.DesativarAsync(c.Id, _ator))).Sucesso);
        Assert.True((await Servico(s => s.DesativarAsync(c.Id, _ator))).Sucesso);
        Assert.True((await Servico(s => s.ReativarAsync(c.Id, _ator))).Sucesso);
        Assert.True((await Servico(s => s.ReativarAsync(c.Id, _ator))).Sucesso);

        var tipos = (await EventosAsync(c.Id)).Select(e => e.Tipo).ToList();
        Assert.Equal(1, tipos.Count(t => t == TipoEventoCadastro.Desativado));
        Assert.Equal(1, tipos.Count(t => t == TipoEventoCadastro.Reativado));
    }

    [FactBanco]
    public async Task Desativar_Inexistente_Retorna404() =>
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.DesativarAsync(Guid.NewGuid(), _ator))));

    [FactBanco]
    public async Task Inativo_ESoVisivelParaQuemPodeVerInativos()
    {
        var c = await CriarAsync(Dados("Inativo Visivel So Ao Gestor"));
        await Servico(s => s.DesativarAsync(c.Id, _ator));

        Assert.False((await Servico(s => s.ObterAsync(c.Id, incluirInativos: false))).Sucesso);
        Assert.True((await Servico(s => s.ObterAsync(c.Id, incluirInativos: true))).Sucesso);

        var comoTecnico = await Servico(s => s.ListarAsync(new FiltroDeClientes("Inativo Visivel", null, null, null), incluirInativos: false));
        Assert.Empty(comoTecnico.Itens);
        var comoGestor = await Servico(s => s.ListarAsync(new FiltroDeClientes("Inativo Visivel", null, null, null), incluirInativos: true));
        Assert.Single(comoGestor.Itens);
    }

    [FactBanco]
    public async Task Tecnico_NaoVeInativos_MesmoPedindoOFiltroAtivoFalso()
    {
        var c = await CriarAsync(Dados("Filtro Forcado"));
        await Servico(s => s.DesativarAsync(c.Id, _ator));

        var pagina = await Servico(s => s.ListarAsync(new FiltroDeClientes("Filtro Forcado", null, Ativo: false, null), incluirInativos: false));

        Assert.Empty(pagina.Itens);
    }

    [FactBanco]
    public async Task Atualizar_ClienteInativo_SoOGestorEdita()
    {
        var c = await CriarAsync();
        await Servico(s => s.DesativarAsync(c.Id, _ator));

        var tecnico = await Servico(s => s.AtualizarAsync(c.Id, Dados("Edicao"), c.Versao, _ator, podeEditarInativo: false));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(tecnico));

        var atual = (await Servico(s => s.ObterAsync(c.Id, true))).Valor!;
        var gestor = await Servico(s => s.AtualizarAsync(c.Id, Dados("Edicao"), atual.Versao, _ator, podeEditarInativo: true));
        Assert.True(gestor.Sucesso);
    }

    // ---------- listagem ----------

    [FactBanco]
    public async Task Listar_BuscaPorNomeFantasiaCidadeEDocumento()
    {
        var marca = "Zeta" + Guid.NewGuid().ToString("N")[..6];
        var c = await CriarAsync(Dados("Razão " + marca) with { NomeFantasia = "Fantasia " + marca, Cidade = "Cidade " + marca, Documento = CnpjValido });

        foreach (var busca in new[] { "Razão " + marca, "Fantasia " + marca, "Cidade " + marca, "11.222.333/0001-81" })
        {
            var pagina = await Servico(s => s.ListarAsync(new FiltroDeClientes(busca, null, null, null), true));
            Assert.Contains(pagina.Itens, i => i.Id == c.Id);
        }
    }

    [FactBanco]
    public async Task Listar_FiltraPorIncompletoEUf_EPagina()
    {
        var marca = "Pag" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 3; i++) await CriarAsync(Dados($"{marca} {i}") with { Uf = "RO" });
        await CriarAsync(Dados($"{marca} 3", CnpjValido) with { Uf = "SP" });

        var incompletosRo = await Servico(s => s.ListarAsync(new FiltroDeClientes(marca, "ro", null, Incompleto: true), true));
        Assert.Equal(3, incompletosRo.Total);

        var pagina2 = await Servico(s => s.ListarAsync(new FiltroDeClientes(marca, null, null, null, Pagina: 2, Tamanho: 3), true));
        Assert.Equal(4, pagina2.Total);
        Assert.Single(pagina2.Itens);
    }

    [FactBanco]
    public async Task Listar_CuringasDoLikeSaoTratadosComoTextoLiteral()
    {
        var marca = "Lit" + Guid.NewGuid().ToString("N")[..6];
        await CriarAsync(Dados($"{marca} Alfa"));
        await CriarAsync(Dados($"{marca} Beta"));

        var porcento = await Servico(s => s.ListarAsync(new FiltroDeClientes("%", null, null, null), true));
        var sublinhado = await Servico(s => s.ListarAsync(new FiltroDeClientes("_", null, null, null), true));
        var colchetes = await Servico(s => s.ListarAsync(new FiltroDeClientes("[a-z]", null, null, null), true));

        Assert.Empty(porcento.Itens);
        Assert.Empty(sublinhado.Itens);
        Assert.Empty(colchetes.Itens);
    }

    [FactBanco]
    public async Task Listar_LimitaOTamanhoDaPaginaACem()
    {
        var pagina = await Servico(s => s.ListarAsync(new FiltroDeClientes(null, null, null, null, Pagina: -3, Tamanho: 100000), true));

        Assert.Equal(1, pagina.Pagina);
        Assert.Equal(100, pagina.Tamanho);
    }
}
