using Jvd.Tests.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Seguranca;

/// <summary>
/// Complementa <see cref="AutorizacaoDosEndpointsTests"/>: em vez de varrer classes por reflexão, inspeciona as rotas
/// REALMENTE registradas na aplicação em execução (controllers, Minimal APIs, ações herdadas, qualquer assembly).
/// Toda rota precisa declarar autorização: política nomeada ou [AllowAnonymous]. Não usa banco.
/// </summary>
public class AutorizacaoDasRotasReaisTests(JvdApiFactory fabrica) : IClassFixture<JvdApiFactory>
{
    private static readonly string[] AnonimasEsperadas =
    [
        "/api/auth/login", "/api/auth/logout", "/api/auth/refresh", "/api/saude"
    ];

    private List<RouteEndpoint> Rotas()
    {
        _ = fabrica.CreateClient(); // garante a aplicação iniciada
        return fabrica.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static string Caminho(RouteEndpoint rota) => "/" + rota.RoutePattern.RawText?.TrimStart('/');

    private static bool Anonima(RouteEndpoint rota) => rota.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    [Fact]
    public void AAplicacaoRegistraRotas() => Assert.NotEmpty(Rotas());

    [Fact]
    public void TodaRota_DeclaraPoliticaNomeadaOuAnonimo()
    {
        var semDeclaracao = Rotas()
            .Where(r => !Anonima(r))
            .Where(r =>
            {
                var autorizacoes = r.Metadata.GetOrderedMetadata<IAuthorizeData>();
                return autorizacoes.Count == 0 || autorizacoes.Any(a => string.IsNullOrWhiteSpace(a.Policy));
            })
            .Select(r => $"{Caminho(r)} ({r.DisplayName})")
            .ToList();

        Assert.True(semDeclaracao.Count == 0,
            "Rotas sem autorização explícita com política nomeada: " + string.Join("; ", semDeclaracao));
    }

    [Fact]
    public void RotasAnonimas_SaoSoAsEsperadas()
    {
        var anonimas = Rotas().Where(Anonima).Select(Caminho).Distinct().Order().ToArray();

        Assert.Equal(AnonimasEsperadas, anonimas);
    }

    [Fact]
    public void RotasDeUsuarios_ExigemSempreAPoliticaGestor()
    {
        var usuarios = Rotas().Where(r => Caminho(r).StartsWith("/api/usuarios", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(usuarios);
        Assert.All(usuarios, r =>
        {
            Assert.False(Anonima(r));
            Assert.Contains(r.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == "Gestor");
        });
    }
}
