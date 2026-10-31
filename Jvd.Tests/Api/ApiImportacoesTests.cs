using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Api;

/// <summary>Permissões da importação decididas ANTES de qualquer acesso a dados: sem banco.</summary>
public class ApiImportacoesSemBancoTests(JvdApiFactory fabrica) : IClassFixture<JvdApiFactory>
{
    private HttpClient ClienteCom(PerfilUsuario? perfil, bool senhaProvisoria = false)
    {
        var cliente = fabrica.CreateClient();
        if (perfil is { } p)
        {
            var emissor = fabrica.Services.GetRequiredService<EmissorDeTokens>();
            var (token, _) = emissor.GerarAccessToken(new Usuario { Id = Guid.NewGuid(), Nome = "Teste", Perfil = p, DeveTrocarSenha = senhaProvisoria });
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return cliente;
    }

    public static IEnumerable<object[]> Rotas() =>
    [
        ["POST", "/api/importacoes/clientes"],
        ["POST", "/api/importacoes/servicos"],
        ["GET", "/api/importacoes"],
        ["GET", $"/api/importacoes/{Guid.NewGuid()}"]
    ];

    private static HttpRequestMessage Pedido(string metodo, string rota) => new(new HttpMethod(metodo), rota)
    {
        Content = metodo == "POST" ? new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "arquivo", "x.xlsx" } } : null
    };

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task SemToken_Retorna401(string metodo, string rota) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClienteCom(null).SendAsync(Pedido(metodo, rota))).StatusCode);

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task Tecnico_NaoImportaNemConsultaHistorico_Retorna403(string metodo, string rota) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await ClienteCom(PerfilUsuario.Tecnico).SendAsync(Pedido(metodo, rota))).StatusCode);

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task GestorComSenhaProvisoria_Retorna403(string metodo, string rota) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await ClienteCom(PerfilUsuario.Gestor, senhaProvisoria: true).SendAsync(Pedido(metodo, rota))).StatusCode);
}

/// <summary>Importação pela API HTTP real, com SQL Server. Planilhas SINTÉTICAS. Só com JVD_TESTES_BANCO=1.</summary>
public class ApiImportacoesComBancoTests : IClassFixture<BancoDeTeste>, IDisposable
{
    private readonly JvdApiFactoryPersonalizada _fabrica;

    public ApiImportacoesComBancoTests(BancoDeTeste banco) => _fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);

    public void Dispose() => _fabrica.Dispose();

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpClient> LogarAsync(PerfilUsuario perfil)
    {
        var (_, email) = await ServicosDeTeste.CriarUsuarioAsync(_fabrica.Services, perfil);
        var cliente = _fabrica.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        cliente.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await LerAsync(login)).GetProperty("accessToken").GetString());
        return cliente;
    }

    private static MultipartFormDataContent Envio(byte[] arquivo, string nome = "planilha.xlsx", string campo = "arquivo") =>
        new() { { new ByteArrayContent(arquivo), campo, nome } };

    private static byte[] Clientes(string marca) => PlanilhaSintetica.PlanilhaDeClientes(
        PlanilhaSintetica.Cliente($"{marca} Um", "11222333000181"),
        PlanilhaSintetica.Cliente($"{marca} Dois", null));

    private async Task<int> ContarClientesAsync(string marca)
    {
        await using var escopo = _fabrica.Services.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<JvdDbContext>().Clientes.CountAsync(c => c.NomeRazaoSocial.StartsWith(marca));
    }

    [FactBanco]
    public async Task SemInformarSimular_AImportacaoEhSimulada_ENadaEGravado()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var marca = "Http" + Guid.NewGuid().ToString("N")[..8];

        var resposta = await gestor.PostAsync("/api/importacoes/clientes", Envio(Clientes(marca)));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await LerAsync(resposta);
        Assert.True(corpo.GetProperty("simulacao").GetBoolean());
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("importacaoId").ValueKind);
        Assert.Equal(2, corpo.GetProperty("totalImportados").GetInt32());
        Assert.Equal(0, await ContarClientesAsync(marca));
    }

    [FactBanco]
    public async Task SimularFalse_GravaORelatorioTrazAvisosSemConteudoEOHistoricoMostra()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var marca = "Http" + Guid.NewGuid().ToString("N")[..8];

        var resposta = await gestor.PostAsync("/api/importacoes/clientes?simular=false", Envio(Clientes(marca), "clientes.xlsx"));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await LerAsync(resposta);
        Assert.False(corpo.GetProperty("simulacao").GetBoolean());
        Assert.Equal(2, await ContarClientesAsync(marca));
        var aviso = corpo.GetProperty("ocorrencias")[0];
        Assert.Equal("Aviso", aviso.GetProperty("gravidade").GetString());
        Assert.DoesNotContain(marca, aviso.GetProperty("motivo").GetString());

        var id = corpo.GetProperty("importacaoId").GetGuid();
        var detalhe = await LerAsync(await gestor.GetAsync($"/api/importacoes/{id}"));
        Assert.Equal("clientes.xlsx", detalhe.GetProperty("importacao").GetProperty("nomeArquivo").GetString());
        Assert.Equal(1, detalhe.GetProperty("ocorrencias").GetArrayLength());

        var lista = await LerAsync(await gestor.GetAsync("/api/importacoes?tamanho=100"));
        Assert.Contains(lista.GetProperty("itens").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
    }

    [FactBanco]
    public async Task MesmoArquivoDeNovo_Retorna409_ComCodigoEstavel()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var arquivo = Clientes("Http" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(HttpStatusCode.OK, (await gestor.PostAsync("/api/importacoes/clientes?simular=false", Envio(arquivo))).StatusCode);

        var segunda = await gestor.PostAsync("/api/importacoes/clientes?simular=false", Envio(arquivo));

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal("importacao.arquivo_ja_importado", (await LerAsync(segunda)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task Servicos_ImportaSoOGrupoServicos()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var b = Random.Shared.Next(100_000, 999_999_999).ToString();
        var arquivo = PlanilhaSintetica.PlanilhaDeMercadorias(
            PlanilhaSintetica.Grupo("PECAS"), PlanilhaSintetica.Item(b + "0", "PEÇA", "1,00"),
            PlanilhaSintetica.Grupo("SERVIÇOS"), PlanilhaSintetica.Subgrupo(),
            PlanilhaSintetica.Item(b + "1", "SERVIÇO A", "10,00"), PlanilhaSintetica.Item(b + "2", "SERVIÇO B", "20,00"));

        var resposta = await gestor.PostAsync("/api/importacoes/servicos?simular=false", Envio(arquivo));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await LerAsync(resposta);
        Assert.Equal(2, corpo.GetProperty("totalImportados").GetInt32());
        Assert.Equal("Servicos", corpo.GetProperty("tipo").GetString());
    }

    [FactBanco]
    public async Task EnviosInvalidos_Retornam400()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);

        // sem o campo "arquivo"
        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PostAsync("/api/importacoes/clientes", Envio(Clientes("x"), campo: "outro"))).StatusCode);
        // extensão errada
        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PostAsync("/api/importacoes/clientes", Envio(Clientes("x"), "clientes.csv"))).StatusCode);
        // conteúdo que não é xlsx
        var naoXlsx = await gestor.PostAsync("/api/importacoes/clientes", Envio(System.Text.Encoding.UTF8.GetBytes("texto"), "x.xlsx"));
        Assert.Equal(HttpStatusCode.BadRequest, naoXlsx.StatusCode);
        Assert.Equal("validacao", (await LerAsync(naoXlsx)).GetProperty("codigo").GetString());
        // arquivo de serviços na rota de clientes
        var errado = PlanilhaSintetica.PlanilhaDeMercadorias(PlanilhaSintetica.Grupo("SERVIÇOS"), PlanilhaSintetica.Item("1", "X", "1,00"));
        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PostAsync("/api/importacoes/clientes", Envio(errado))).StatusCode);
    }

    [FactBanco]
    public async Task Importacao_Inexistente_Retorna404()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);

        Assert.Equal(HttpStatusCode.NotFound, (await gestor.GetAsync($"/api/importacoes/{Guid.NewGuid()}")).StatusCode);
    }
}
