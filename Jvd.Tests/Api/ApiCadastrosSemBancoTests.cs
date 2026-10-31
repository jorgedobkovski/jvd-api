using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Tests.Suporte;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Api;

/// <summary>
/// Permissões de clientes e equipamentos decididas ANTES de qualquer acesso a dados (autorização), portanto sem
/// banco. Prova que o técnico não desativa/reativa e que sem sessão válida nada passa.
/// </summary>
public class ApiCadastrosSemBancoTests(JvdApiFactory fabrica) : IClassFixture<JvdApiFactory>
{
    private static readonly string[] Recursos = ["clientes", "equipamentos"];

    private HttpClient ClienteCom(PerfilUsuario? perfil, bool senhaProvisoria = false)
    {
        var cliente = fabrica.CreateClient();
        if (perfil is { } p)
        {
            var emissor = fabrica.Services.GetRequiredService<EmissorDeTokens>();
            var (token, _) = emissor.GerarAccessToken(new Usuario
            {
                Id = Guid.NewGuid(), Nome = "Teste", Perfil = p, DeveTrocarSenha = senhaProvisoria
            });
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return cliente;
    }

    public static IEnumerable<object[]> RotasDeLeituraEEscrita() =>
        Recursos.SelectMany(r => new[]
        {
            new object[] { HttpMethod.Get.Method, $"/api/{r}" },
            new object[] { HttpMethod.Get.Method, $"/api/{r}/{Guid.NewGuid()}" },
            new object[] { HttpMethod.Post.Method, $"/api/{r}" },
            new object[] { HttpMethod.Put.Method, $"/api/{r}/{Guid.NewGuid()}" },
            new object[] { HttpMethod.Post.Method, $"/api/{r}/{Guid.NewGuid()}/desativar" },
            new object[] { HttpMethod.Post.Method, $"/api/{r}/{Guid.NewGuid()}/reativar" }
        });

    public static IEnumerable<object[]> RotasSoDoGestor() =>
        Recursos.SelectMany(r => new[]
        {
            new object[] { $"/api/{r}/{Guid.NewGuid()}/desativar" },
            new object[] { $"/api/{r}/{Guid.NewGuid()}/reativar" }
        });

    private static HttpRequestMessage Pedido(string metodo, string rota) =>
        new(new HttpMethod(metodo), rota) { Content = metodo == "GET" ? null : JsonContent.Create(new { }) };

    [Theory]
    [MemberData(nameof(RotasDeLeituraEEscrita))]
    public async Task SemToken_Retorna401(string metodo, string rota)
    {
        var resposta = await ClienteCom(null).SendAsync(Pedido(metodo, rota));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RotasDeLeituraEEscrita))]
    public async Task ComSenhaProvisoria_Retorna403_MesmoSendoGestor(string metodo, string rota)
    {
        var resposta = await ClienteCom(PerfilUsuario.Gestor, senhaProvisoria: true).SendAsync(Pedido(metodo, rota));

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RotasSoDoGestor))]
    public async Task Tecnico_NaoDesativaNemReativa_Retorna403(string rota)
    {
        var resposta = await ClienteCom(PerfilUsuario.Tecnico).SendAsync(Pedido("POST", rota));

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    // ---------- emissor e catálogo (Fase 2B-2) ----------

    public static IEnumerable<object[]> RotasDeEmissorECatalogo()
    {
        var id = Guid.NewGuid();
        return new[]
        {
            new object[] { "GET", "/api/emissor" },
            new object[] { "PUT", "/api/emissor" },
            new object[] { "GET", "/api/catalogo-servicos" },
            new object[] { "GET", $"/api/catalogo-servicos/{id}" },
            new object[] { "POST", "/api/catalogo-servicos" },
            new object[] { "PUT", $"/api/catalogo-servicos/{id}" },
            new object[] { "POST", $"/api/catalogo-servicos/{id}/desativar" },
            new object[] { "POST", $"/api/catalogo-servicos/{id}/reativar" }
        };
    }

    public static IEnumerable<object[]> RotasDeEscritaDoGestor() =>
        RotasDeEmissorECatalogo().Where(r => (string)r[0] != "GET");

    [Theory]
    [MemberData(nameof(RotasDeEmissorECatalogo))]
    public async Task EmissorECatalogo_SemToken_Retorna401(string metodo, string rota)
    {
        var resposta = await ClienteCom(null).SendAsync(Pedido(metodo, rota));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RotasDeEmissorECatalogo))]
    public async Task EmissorECatalogo_ComSenhaProvisoria_Retorna403_MesmoSendoGestor(string metodo, string rota)
    {
        var resposta = await ClienteCom(PerfilUsuario.Gestor, senhaProvisoria: true).SendAsync(Pedido(metodo, rota));

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RotasDeEscritaDoGestor))]
    public async Task EmissorECatalogo_Tecnico_NaoEscreve_Retorna403(string metodo, string rota)
    {
        var resposta = await ClienteCom(PerfilUsuario.Tecnico).SendAsync(Pedido(metodo, rota));

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }
}
