using System.Reflection;
using jvd_api.Seguranca;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Jvd.Tests.Seguranca;

/// <summary>
/// Trava de segurança para as próximas fases: TODO endpoint precisa declarar a autorização de forma explícita,
/// com política NOMEADA ou [AllowAnonymous]. Um [Authorize] puro (sem política) cairia na política padrão e esconderia
/// a intenção; endpoint sem nenhum atributo depende só do fallback. Quem criar endpoint novo vê este teste falhar.
/// </summary>
public class AutorizacaoDosEndpointsTests
{
    private static readonly string[] PoliticasConhecidas = [Politicas.Tecnico, Politicas.Gestor, Politicas.Autenticado];

    private static IEnumerable<(Type Controller, MethodInfo Acao)> Endpoints() =>
        typeof(Politicas).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => (t, m)));

    [Fact]
    public void ExistemEndpointsParaVerificar() => Assert.NotEmpty(Endpoints());

    [Fact]
    public void TodoEndpoint_DeclaraAutorizacaoExplicita_ComPoliticaNomeadaOuAnonimo()
    {
        var semDeclaracao = new List<string>();

        foreach (var (controller, acao) in Endpoints())
        {
            var anonimo = acao.GetCustomAttributes<AllowAnonymousAttribute>().Any()
                          || controller.GetCustomAttributes<AllowAnonymousAttribute>().Any();

            var politicas = acao.GetCustomAttributes<AuthorizeAttribute>()
                .Concat(controller.GetCustomAttributes<AuthorizeAttribute>())
                .Select(a => a.Policy)
                .ToList();

            var temPoliticaNomeada = politicas.Count > 0 && politicas.All(p => !string.IsNullOrWhiteSpace(p));

            if (!anonimo && !temPoliticaNomeada)
            {
                semDeclaracao.Add($"{controller.Name}.{acao.Name}");
            }
        }

        Assert.True(semDeclaracao.Count == 0,
            "Endpoints sem autorização explícita (use [Authorize(Policy = ...)] ou [AllowAnonymous]): " + string.Join(", ", semDeclaracao));
    }

    [Fact]
    public void Toda_PoliticaUsadaNosEndpoints_EhUmaPoliticaConhecida()
    {
        var usadas = Endpoints()
            .SelectMany(e => e.Acao.GetCustomAttributes<AuthorizeAttribute>().Concat(e.Controller.GetCustomAttributes<AuthorizeAttribute>()))
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();

        Assert.All(usadas, p => Assert.Contains(p, PoliticasConhecidas));
    }

    [Fact]
    public void Endpoints_AnonimosSaoSoOsEsperados()
    {
        var anonimos = Endpoints()
            .Where(e => e.Acao.GetCustomAttributes<AllowAnonymousAttribute>().Any()
                        || e.Controller.GetCustomAttributes<AllowAnonymousAttribute>().Any())
            .Select(e => $"{e.Controller.Name}.{e.Acao.Name}")
            .Order()
            .ToArray();

        // Somente: saúde e o ciclo de autenticação (login/renovar/sair). Qualquer outro endpoint anônimo precisa de decisão consciente.
        Assert.Equal(
            ["AutenticacaoController.Login", "AutenticacaoController.Renovar", "AutenticacaoController.Sair", "SaudeController.Verificar"],
            anonimos);
    }

    [Fact]
    public void ControllerDeUsuarios_ExigeSempreOGestor()
    {
        var acoes = Endpoints().Where(e => e.Controller.Name == "UsuariosController").ToList();

        Assert.NotEmpty(acoes);
        Assert.All(acoes, e =>
        {
            var politicas = e.Acao.GetCustomAttributes<AuthorizeAttribute>().Concat(e.Controller.GetCustomAttributes<AuthorizeAttribute>());
            Assert.Contains(politicas, a => a.Policy == Politicas.Gestor);
            Assert.False(e.Acao.GetCustomAttributes<AllowAnonymousAttribute>().Any());
        });
    }
}
