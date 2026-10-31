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

    private static ClaimsPrincipal Usuario(string? perfil)
    {
        if (perfil is null)
        {
            return new ClaimsPrincipal(new ClaimsIdentity()); // anônimo (sem tipo de autenticação)
        }

        var identidade = new ClaimsIdentity(
            [new Claim(NomesDeClaims.Perfil, perfil)],
            authenticationType: "teste", nameType: NomesDeClaims.Nome, roleType: NomesDeClaims.Perfil);
        return new ClaimsPrincipal(identidade);
    }

    private static async Task<bool> Autorizado(string politica, string? perfil)
    {
        using var provedor = Provedor();
        var servico = provedor.GetRequiredService<IAuthorizationService>();
        var resultado = await servico.AuthorizeAsync(Usuario(perfil), resource: null, politica);
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
