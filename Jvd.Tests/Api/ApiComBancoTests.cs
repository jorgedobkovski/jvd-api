using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jvd.Domain;
using Jvd.Infrastructure.Identity;
using Jvd.Tests.Suporte;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Api;

/// <summary>Fluxo completo com SQL Server real (banco descartável). Roda só com JVD_TESTES_BANCO=1.</summary>
public class ApiComBancoTests : IClassFixture<BancoDeTeste>, IDisposable
{
    private readonly JvdApiFactoryPersonalizada _fabrica;

    private readonly BancoDeTeste _banco;

    public ApiComBancoTests(BancoDeTeste banco)
    {
        _banco = banco;
        _fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);
    }

    public void Dispose() => _fabrica.Dispose();

    private async Task<string> CriarUsuarioAsync(PerfilUsuario perfil = PerfilUsuario.Tecnico)
    {
        await using var escopo = _fabrica.Services.CreateAsyncScope();
        var usuarios = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var email = DadosDeTeste.NovoEmail();
        var resultado = await usuarios.CreateAsync(new Usuario
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, Nome = "Usuário HTTP",
            Perfil = perfil, CriadoEm = DateTime.UtcNow, AtualizadoEm = DateTime.UtcNow
        }, DadosDeTeste.SenhaValida);
        Assert.True(resultado.Succeeded);
        return email;
    }

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    [FactBanco]
    public async Task FluxoCompleto_Login_Me_Renovar_Logout()
    {
        var email = await CriarUsuarioAsync(PerfilUsuario.Gestor);
        var cliente = _fabrica.CreateClient();

        // login
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("no-store", login.Headers.CacheControl!.ToString());
        var tokens = await LerAsync(login);
        var access = tokens.GetProperty("accessToken").GetString()!;
        var refresh = tokens.GetProperty("refreshToken").GetString()!;
        Assert.Equal("Gestor", tokens.GetProperty("usuario").GetProperty("perfil").GetString());

        // me com o access token
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var me = await cliente.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // renovar
        var renovado = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.OK, renovado.StatusCode);
        var novoRefresh = (await LerAsync(renovado)).GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(refresh, novoRefresh);

        // o refresh antigo não vale mais; reapresentado logo em seguida (pedido simultâneo) falha SEM derrubar a sessão nova
        var reuso = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);
        var aposReuso = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = novoRefresh });
        Assert.Equal(HttpStatusCode.OK, aposReuso.StatusCode);
    }

    [FactBanco]
    public async Task Reuso_ComToleranciaZero_DerrubaAsSessoes()
    {
        using var fabrica = new JvdApiFactoryPersonalizada(_banco.Conexao, new Dictionary<string, string?>
        {
            ["Jwt:RefreshToleranciaSegundos"] = "0"
        });
        string email;
        await using (var escopo = fabrica.Services.CreateAsyncScope())
        {
            var usuarios = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            email = DadosDeTeste.NovoEmail();
            await usuarios.CreateAsync(new Usuario
            {
                Id = Guid.NewGuid(), UserName = email, Email = email, Nome = "Usuário HTTP",
                Perfil = PerfilUsuario.Tecnico, CriadoEm = DateTime.UtcNow, AtualizadoEm = DateTime.UtcNow
            }, DadosDeTeste.SenhaValida);
        }
        var cliente = fabrica.CreateClient();
        var login = await LerAsync(await cliente.PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida }));
        var refresh = login.GetProperty("refreshToken").GetString()!;
        var novoRefresh = (await LerAsync(await cliente.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh })))
            .GetProperty("refreshToken").GetString()!;

        var reuso = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        var aposReuso = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = novoRefresh });

        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, aposReuso.StatusCode);
    }

    [FactBanco]
    public async Task Logout_RevogaOToken()
    {
        var email = await CriarUsuarioAsync();
        var cliente = _fabrica.CreateClient();
        var login = await LerAsync(
            await cliente.PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida }));
        var refresh = login.GetProperty("refreshToken").GetString()!;

        var logout = await cliente.PostAsJsonAsync("/api/auth/logout", new { refreshToken = refresh });
        var renovar = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, renovar.StatusCode);
    }

    [FactBanco]
    public async Task Login_ComSenhaErrada_Retorna401GenericoSemEcoarDados()
    {
        var email = await CriarUsuarioAsync();

        var resposta = await _fabrica.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, senha = "SenhaErrada#999" });
        var corpo = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Contains("E-mail ou senha inválidos", corpo);
        Assert.DoesNotContain(email, corpo);
        Assert.DoesNotContain("SenhaErrada#999", corpo);
    }

    [FactBanco]
    public async Task Login_EmailInexistenteESenhaErrada_RespondemIgual()
    {
        var existente = await CriarUsuarioAsync();
        var cliente = _fabrica.CreateClient();

        var a = await cliente.PostAsJsonAsync("/api/auth/login", new { email = existente, senha = "SenhaErrada#999" });
        var b = await cliente.PostAsJsonAsync("/api/auth/login", new { email = DadosDeTeste.NovoEmail(), senha = "SenhaErrada#999" });

        Assert.Equal(a.StatusCode, b.StatusCode);
        Assert.Equal(
            (await LerAsync(a)).GetProperty("detail").GetString(),
            (await LerAsync(b)).GetProperty("detail").GetString());
    }
}
