using Jvd.Application.Comum;
using Jvd.Application.Importacao;
using Jvd.Domain;
using Jvd.Infrastructure;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Importacao;

/// <summary>
/// Importação das planilhas sobre SQL Server real (banco descartável). Planilhas e dados 100% SINTÉTICOS — a carga
/// real de data/ é uma operação à parte. Só com JVD_TESTES_BANCO=1.
/// </summary>
public class ImportacaoServiceTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private const string Cnpj = "11222333000181";

    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly InterceptorDeFalha _falha = new();
    private ServiceProvider _sp = null!;
    private Guid _gestor;

    public ImportacaoServiceTests(BancoDeTeste banco) => _banco = banco;

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
        _gestor = (await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor)).Id;
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

    /// <summary>Quando armado, faz qualquer gravação que inclua um registro de importação falhar (queda de conexão simulada).</summary>
    private sealed class InterceptorDeFalha : SaveChangesInterceptor
    {
        public volatile bool Armado;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> resultado, CancellationToken ct = default)
        {
            if (Armado && eventData.Context!.ChangeTracker.Entries<Domain.Importacao>().Any(e => e.State == EntityState.Added))
            {
                throw new TimeoutException("falha simulada ao gravar a importação");
            }

            return base.SavingChangesAsync(eventData, resultado, ct);
        }
    }

    // ---------- apoio ----------

    private async Task<T> Servico<T>(Func<IImportacaoService, Task<T>> acao)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IImportacaoService>());
    }

    private Task<Resultado<RelatorioDeImportacao>> Clientes(byte[] arquivo, bool simular, string nome = "clientes.xlsx") =>
        Servico(s => s.ImportarClientesAsync(new MemoryStream(arquivo), nome, simular, _gestor));

    private Task<Resultado<RelatorioDeImportacao>> Servicos(byte[] arquivo, bool simular, string nome = "mercadorias.xlsx") =>
        Servico(s => s.ImportarServicosAsync(new MemoryStream(arquivo), nome, simular, _gestor));

    private async Task<TResult> Banco<TResult>(Func<JvdDbContext, Task<TResult>> consulta)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await consulta(escopo.ServiceProvider.GetRequiredService<JvdDbContext>());
    }

    /// <summary>Os códigos do ERP são numéricos: a base dos códigos de teste também.</summary>
    private static string CodigoNumerico() => Random.Shared.Next(100_000, 999_999_999).ToString();

    private static string Marca() => "Imp" + Guid.NewGuid().ToString("N")[..8];

    private static byte[] PlanilhaComClientes(string marca, int quantidade = 3)
    {
        var linhas = new List<IReadOnlyDictionary<string, string>>();
        for (var i = 0; i < quantidade; i++)
        {
            linhas.Add(PlanilhaSintetica.Cliente($"{marca} Cliente {i}", i == 0 ? Cnpj : i == 1 ? null : DadosDeTeste.NovoCpf(),
                inativo: i == 2 ? "SIM" : "NÃO"));
        }

        return PlanilhaSintetica.PlanilhaDeClientes(linhas.ToArray());
    }

    private static byte[] PlanilhaDeServicos(string codigoBase, params (string Codigo, string Descricao, string? Preco, string Inativo)[] itens) =>
        PlanilhaSintetica.PlanilhaDeMercadorias(
            new[] { PlanilhaSintetica.Grupo("PECAS"), PlanilhaSintetica.Item(codigoBase + "9", "PEÇA QUALQUER", "1,00"), PlanilhaSintetica.Grupo("SERVIÇOS"), PlanilhaSintetica.Subgrupo() }
                .Concat(itens.Select(i => PlanilhaSintetica.Item(i.Codigo, i.Descricao, i.Preco, i.Inativo))).ToArray());

    private static string? Codigo(Resultado r) => r.Erros.FirstOrDefault()?.Codigo;

    // ---------- simulação ----------

    [FactBanco]
    public async Task Simular_ValidaEDevolveORelatorio_SemGravarNada()
    {
        var marca = Marca();
        var antesImportacoes = await Banco(db => db.Importacoes.CountAsync());
        var antesClientes = await Banco(db => db.Clientes.CountAsync());

        var r = await Clientes(PlanilhaComClientes(marca), simular: true);

        Assert.True(r.Sucesso);
        Assert.True(r.Valor!.Simulacao);
        Assert.Null(r.Valor.ImportacaoId);
        Assert.Equal(3, r.Valor.TotalLidos);
        Assert.Equal(3, r.Valor.TotalImportados);
        Assert.Equal(antesImportacoes, await Banco(db => db.Importacoes.CountAsync()));
        Assert.Equal(antesClientes, await Banco(db => db.Clientes.CountAsync()));
    }

    [FactBanco]
    public async Task Simular_NaoTrancaAImportacaoReal_EPodeSerRepetida()
    {
        var arquivo = PlanilhaComClientes(Marca());

        Assert.True((await Clientes(arquivo, simular: true)).Sucesso);
        Assert.True((await Clientes(arquivo, simular: true)).Sucesso);
        var real = await Clientes(arquivo, simular: false);

        Assert.True(real.Sucesso);
        Assert.NotNull(real.Valor!.ImportacaoId);
    }

    // ---------- importação real: clientes ----------

    [FactBanco]
    public async Task ImportarClientes_Grava_ComOrigemImportado_IncompletoEInativoCorretos()
    {
        var marca = Marca();

        var r = await Clientes(PlanilhaComClientes(marca), simular: false);

        Assert.True(r.Sucesso);
        Assert.False(r.Valor!.Simulacao);
        Assert.Equal(3, r.Valor.TotalImportados);
        var clientes = await Banco(db => db.Clientes.AsNoTracking().Where(c => c.NomeRazaoSocial.StartsWith(marca)).OrderBy(c => c.NomeRazaoSocial).ToListAsync());
        Assert.Equal(3, clientes.Count);
        Assert.All(clientes, c => Assert.Equal(OrigemCadastro.Importado, c.Origem));
        Assert.Equal([false, true, false], clientes.Select(c => c.CadastroIncompleto).ToArray()); // o 2º não tem documento
        Assert.Equal([true, true, false], clientes.Select(c => c.Ativo).ToArray());               // o 3º é "SIM" inativo
        Assert.Equal(Cnpj, clientes[0].Documento);
    }

    [FactBanco]
    public async Task ImportarClientes_RegistraAImportacao_ComHashAtorTotaisEOcorrenciasSemConteudo()
    {
        var marca = Marca();
        var arquivo = PlanilhaComClientes(marca);

        var r = await Clientes(arquivo, simular: false, nome: @"C:\pasta\secreta\clientes-reais.xlsx");

        var id = r.Valor!.ImportacaoId!.Value;
        var registro = await Banco(db => db.Importacoes.AsNoTracking().SingleAsync(i => i.Id == id));
        Assert.Equal(TipoImportacao.Clientes, registro.Tipo);
        Assert.Equal("clientes-reais.xlsx", registro.NomeArquivo);          // só o nome, sem a pasta
        Assert.Equal(_gestor, registro.UsuarioId);
        Assert.Equal(64, registro.HashArquivo.Length);
        Assert.Equal(3, registro.TotalImportados);
        Assert.Equal(1, registro.TotalAvisos);                               // o cliente sem documento
        Assert.Equal(0, registro.TotalRejeitados);
        Assert.Equal(r.Valor.TotalIgnoradas, registro.TotalIgnoradas);

        var erros = await Banco(db => db.ImportacoesErros.AsNoTracking().Where(e => e.ImportacaoId == id).ToListAsync());
        var ocorrencia = Assert.Single(erros);
        Assert.Equal(GravidadeOcorrencia.Aviso, ocorrencia.Gravidade);
        Assert.DoesNotContain(marca, ocorrencia.Motivo);
        Assert.True(ocorrencia.Linha > 0);
    }

    [FactBanco]
    public async Task ImportarClientes_MesmoArquivoDeNovo_EBarrado_InclusiveNaSimulacao()
    {
        var arquivo = PlanilhaComClientes(Marca());
        Assert.True((await Clientes(arquivo, simular: false)).Sucesso);

        var segunda = await Clientes(arquivo, simular: false);
        var simulada = await Clientes(arquivo, simular: true);

        Assert.Equal(CodigosDeErro.ArquivoJaImportado, Codigo(segunda));
        Assert.Equal(CodigosDeErro.ArquivoJaImportado, Codigo(simulada));
    }

    [FactBanco]
    public async Task ImportarClientes_ArquivoDiferenteMasMesmoConteudoLogico_NaoEBarrado()
    {
        var a = await Clientes(PlanilhaComClientes(Marca()), simular: false);
        var b = await Clientes(PlanilhaComClientes(Marca()), simular: false);

        Assert.True(a.Sucesso);
        Assert.True(b.Sucesso);
    }

    [FactBanco]
    public async Task ImportarClientes_DoisEnviosSimultaneosDoMesmoArquivo_UmVence()
    {
        var arquivo = PlanilhaComClientes(Marca());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => Clientes(arquivo, simular: false))));

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        Assert.All(resultados.Where(r => !r.Sucesso),
            r => Assert.Contains(Codigo(r), new[] { CodigosDeErro.ArquivoJaImportado, CodigosDeErro.Conflito }));
        var hash = await Banco(db => db.Importacoes.AsNoTracking().OrderByDescending(i => i.ExecutadaEm).Select(i => i.HashArquivo).FirstAsync());
        Assert.Equal(1, await Banco(db => db.Importacoes.CountAsync(i => i.HashArquivo == hash)));
    }

    [FactBanco]
    public async Task ImportarClientes_FalhaAoGravar_NaoDeixaNadaPelaMetade_EPodeTentarDeNovo()
    {
        var marca = Marca();
        var arquivo = PlanilhaComClientes(marca);

        _falha.Armado = true;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Clientes(arquivo, simular: false));
        }
        finally
        {
            _falha.Armado = false;
        }

        Assert.Equal(0, await Banco(db => db.Clientes.CountAsync(c => c.NomeRazaoSocial.StartsWith(marca))));
        var novamente = await Clientes(arquivo, simular: false);
        Assert.True(novamente.Sucesso);
        Assert.Equal(3, await Banco(db => db.Clientes.CountAsync(c => c.NomeRazaoSocial.StartsWith(marca))));
    }

    // ---------- arquivos inválidos ----------

    [FactBanco]
    public async Task ArquivoDeMercadoriasEnviadoComoClientes_EVice_Versa_EhRecusado_SemGravarNada()
    {
        var antes = await Banco(db => db.Importacoes.CountAsync());
        var mercadorias = PlanilhaDeServicos("1", ("100", "SERVICO", "10,00", "NÃO"));
        var clientes = PlanilhaComClientes(Marca());

        var r1 = await Clientes(mercadorias, simular: false);
        var r2 = await Servicos(clientes, simular: false);

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r1));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(r2));
        Assert.Equal(antes, await Banco(db => db.Importacoes.CountAsync()));
    }

    [FactBanco]
    public async Task ArquivoQueNaoEXlsx_EhRecusado()
    {
        var r = await Clientes(System.Text.Encoding.UTF8.GetBytes("não sou uma planilha"), simular: true);

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    [FactBanco]
    public async Task ArquivoAcimaDe10Mb_EhRecusado()
    {
        var r = await Clientes(new byte[ImportacaoServiceLimite + 1], simular: true);

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    private const int ImportacaoServiceLimite = 10 * 1024 * 1024;

    [FactBanco]
    public async Task PlanilhaDeClientesSemNenhumCliente_EhRecusada()
    {
        var r = await Clientes(PlanilhaSintetica.PlanilhaDeClientes(), simular: true);

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    // ---------- catálogo de serviços ----------

    [FactBanco]
    public async Task ImportarServicos_Grava_SoOGrupoServicos_ComPrecoInativoECodigo()
    {
        var b = CodigoNumerico();
        var arquivo = PlanilhaDeServicos(b,
            (b + "1", "MAO DE OBRA", "70,00", "NÃO"),
            (b + "2", "TROCA DO TECLADO", "1.180,50", "NÃO"),
            (b + "3", "ANTIGO", "5,00", "SIM"),
            (b + "4", "SEM PRECO", null, "NÃO"));

        var r = await Servicos(arquivo, simular: false);

        Assert.True(r.Sucesso);
        Assert.Equal(4, r.Valor!.TotalLidos);
        Assert.Equal(4, r.Valor.TotalImportados);
        Assert.Equal(1, r.Valor.TotalAvisos);          // item sem preço
        Assert.True(r.Valor.TotalIgnoradas >= 3);      // cabeçalho, grupo de peças com 1 item e subgrupo
        var itens = await Banco(db => db.Catalogo.AsNoTracking().Where(s => s.CodigoOrigem!.StartsWith(b)).OrderBy(s => s.CodigoOrigem).ToListAsync());
        Assert.Equal(4, itens.Count);
        Assert.Equal([70.00m, 1180.50m, 5.00m, null], itens.Select(i => i.PrecoSugerido).ToArray());
        Assert.Equal([true, true, false, true], itens.Select(i => i.Ativo).ToArray());
        Assert.DoesNotContain(await Banco(db => db.Catalogo.AsNoTracking().Select(s => s.Descricao).ToListAsync()), d => d == "PEÇA QUALQUER");
    }

    [FactBanco]
    public async Task ImportarServicos_CodigoJaCadastrado_OuRepetidoNoArquivo_ERejeitado_ListadoComoErro()
    {
        var b = CodigoNumerico();
        Assert.True((await Servicos(PlanilhaDeServicos(b, (b + "1", "PRIMEIRO", "10,00", "NÃO")), simular: false)).Sucesso);

        var arquivo = PlanilhaDeServicos(b + "x",
            (b + "1", "MESMO CODIGO DE ANTES", "20,00", "NÃO"),
            (b + "2", "NOVO", "30,00", "NÃO"),
            (b + "2", "NOVO REPETIDO", "31,00", "NÃO"));

        var r = await Servicos(arquivo, simular: false);

        Assert.True(r.Sucesso);
        Assert.Equal(3, r.Valor!.TotalLidos);
        Assert.Equal(1, r.Valor.TotalImportados);
        Assert.Equal(2, r.Valor.TotalRejeitados);
        Assert.All(r.Valor.Ocorrencias.Where(o => o.Gravidade == GravidadeOcorrencia.Erro), o => Assert.DoesNotContain(b, o.Motivo));
        var gravados = await Banco(db => db.Catalogo.AsNoTracking().Where(s => s.CodigoOrigem!.StartsWith(b)).Select(s => s.Descricao).ToListAsync());
        Assert.Equal(["NOVO", "PRIMEIRO"], gravados.Order().ToArray());
        var erros = await Banco(db => db.ImportacoesErros.CountAsync(e => e.ImportacaoId == r.Valor.ImportacaoId && e.Gravidade == GravidadeOcorrencia.Erro));
        Assert.Equal(2, erros);
    }

    [FactBanco]
    public async Task ImportarServicos_SemOGrupoServicos_EhRecusado()
    {
        var arquivo = PlanilhaSintetica.PlanilhaDeMercadorias(PlanilhaSintetica.Grupo("PECAS"), PlanilhaSintetica.Item("1", "X", "1,00"));

        var r = await Servicos(arquivo, simular: false);

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    // ---------- histórico ----------

    [FactBanco]
    public async Task Historico_ListaMaisRecentesPrimeiro_EDetalheTrazAsOcorrencias()
    {
        var primeira = await Clientes(PlanilhaComClientes(Marca()), simular: false);
        _relogio.Avancar(TimeSpan.FromMinutes(5));
        var segunda = await Clientes(PlanilhaComClientes(Marca()), simular: false);

        var pagina = await Servico(s => s.ListarAsync(1, 100));
        var ids = pagina.Itens.Select(i => i.Id).ToList();
        Assert.True(ids.IndexOf(segunda.Valor!.ImportacaoId!.Value) < ids.IndexOf(primeira.Valor!.ImportacaoId!.Value));

        var detalhe = await Servico(s => s.ObterAsync(primeira.Valor.ImportacaoId!.Value));
        Assert.True(detalhe.Sucesso);
        Assert.Equal(primeira.Valor.TotalImportados, detalhe.Valor!.Resumo.TotalImportados);
        Assert.Equal(primeira.Valor.Ocorrencias.Count, detalhe.Valor.Ocorrencias.Count);

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await Servico(s => s.ObterAsync(Guid.NewGuid()))));
    }

    [FactBanco]
    public async Task Historico_LimitaOTamanhoDaPaginaACem()
    {
        var pagina = await Servico(s => s.ListarAsync(-4, 100000));

        Assert.Equal(1, pagina.Pagina);
        Assert.Equal(100, pagina.Tamanho);
    }
}
