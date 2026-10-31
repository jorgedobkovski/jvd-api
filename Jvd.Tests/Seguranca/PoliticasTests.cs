using System.Security.Claims;
using jvd_api.Seguranca;
using Jvd.Infrastructure.Autenticacao;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Seguranca;

public class PoliticasTests
{
    private static ServiceProvider Provedor() =>
        new ServiceCollection().AddLogging().AddAuthorization(Politicas.Configurar).BuildServiceProvider();

    private static ClaimsPrincipal Usuario(string? perfil, bool trocaDeSenhaPendente = false)
    {
        if (perfil is null)
        {
            return new ClaimsPrincipal(new ClaimsIdentity()); // anônimo (sem tipo de autenticação)
        }

        var claims = new List<Claim> { new(NomesDeClaims.Perfil, perfil) };
        if (trocaDeSenhaPendente) claims.Add(new Claim(NomesDeClaims.TrocarSenha, "true"));

        var identidade = new ClaimsIdentity(
            claims, authenticationType: "teste", nameType: NomesDeClaims.Nome, roleType: NomesDeClaims.Perfil);
        return new ClaimsPrincipal(identidade);
    }

    private static async Task<bool> Autorizado(string politica, string? perfil, bool trocaDeSenhaPendente = false)
    {
        using var provedor = Provedor();
        var servico = provedor.GetRequiredService<IAuthorizationService>();
        var resultado = await servico.AuthorizeAsync(Usuario(perfil, trocaDeSenhaPendente), resource: null, politica);
        return resultado.Succeeded;
    }

    [Theory]
    [InlineData("Tecnico", true)]
    [InlineData("Gestor", true)]   // o gestor herda tudo do técnico
    [InlineData("Outro", false)]
    [InlineData(null, false)]      // anônimo
    public async Task PoliticaTecnico_AceitaTecnicoEGestor(string? perfil, bool esperado) =>
        Assert.Equal(esperado, await Autorizado(Politicas.Tecnico, perfil));

    [Theory]
    [InlineData("Gestor", true)]
    [InlineData("Tecnico", false)] // técnico não faz o que é do gestor (importar, ver valores…)
    [InlineData("Outro", false)]
    [InlineData(null, false)]
    public async Task PoliticaGestor_AceitaSomenteGestor(string? perfil, bool esperado) =>
        Assert.Equal(esperado, await Autorizado(Politicas.Gestor, perfil));

    [Theory]
    [InlineData("Tecnico", false)]  // com senha provisória, NENHUMA política de perfil libera o acesso
    [InlineData("Gestor", false)]
    public async Task SenhaProvisoria_BloqueiaAsPoliticasDePerfil(string perfil, bool esperado)
    {
        Assert.Equal(esperado, await Autorizado(Politicas.Tecnico, perfil, trocaDeSenhaPendente: true));
        Assert.Equal(esperado, await Autorizado(Politicas.Gestor, perfil, trocaDeSenhaPendente: true));
    }

    [Theory]
    [InlineData("Tecnico", true, true)]
    [InlineData("Gestor", true, true)]
    [InlineData("Tecnico", false, true)]
    [InlineData(null, false, false)] // anônimo nunca
    public async Task PoliticaAutenticado_LiberaMesmoComSenhaProvisoria(string? perfil, bool trocaPendente, bool esperado) =>
        Assert.Equal(esperado, await Autorizado(Politicas.Autenticado, perfil, trocaPendente));

    [Fact]
    public async Task PoliticaDefault_DoAuthorizePuro_BloqueiaQuemTemSenhaProvisoria()
    {
        using var provedor = Provedor();
        var padrao = await provedor.GetRequiredService<IAuthorizationPolicyProvider>().GetDefaultPolicyAsync();
        var servico = provedor.GetRequiredService<IAuthorizationService>();

        Assert.False((await servico.AuthorizeAsync(Usuario("Tecnico", trocaDeSenhaPendente: true), null, padrao)).Succeeded);
        Assert.False((await servico.AuthorizeAsync(Usuario(null), null, padrao)).Succeeded);
        Assert.True((await servico.AuthorizeAsync(Usuario("Tecnico"), null, padrao)).Succeeded);
    }

    [Fact]
    public async Task PoliticaPadrao_BloqueiaQuemTemSenhaProvisoria()
    {
        using var provedor = Provedor();
        var fallback = (await provedor.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync())!;
        var servico = provedor.GetRequiredService<IAuthorizationService>();

        Assert.False((await servico.AuthorizeAsync(Usuario("Gestor", trocaDeSenhaPendente: true), null, fallback)).Succeeded);
        Assert.True((await servico.AuthorizeAsync(Usuario("Gestor"), null, fallback)).Succeeded);
    }

    [Fact]
    public async Task PoliticaPadrao_ExigeUsuarioAutenticado()
    {
        using var provedor = Provedor();
        var fallback = await provedor.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        var servico = provedor.GetRequiredService<IAuthorizationService>();

        Assert.NotNull(fallback);
        Assert.False((await servico.AuthorizeAsync(Usuario(null), null, fallback)).Succeeded);
        Assert.True((await servico.AuthorizeAsync(Usuario("Tecnico"), null, fallback)).Succeeded);
    }

    [Fact]
    public void PerfilNoToken_UsaOsMesmosNomesDoEnum()
    {
        // A política compara com nameof(PerfilUsuario.*); o token grava Perfil.ToString().
        Assert.Equal("Tecnico", Jvd.Domain.PerfilUsuario.Tecnico.ToString());
        Assert.Equal("Gestor", Jvd.Domain.PerfilUsuario.Gestor.ToString());
    }
}
