using Jvd.Tests.Suporte;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Jvd.Tests.Api;

/// <summary>
/// Base das fábricas de teste: sobe a API em memória no ambiente "Testing" (NÃO carrega
/// appsettings.Development.json), sempre com configuração explícita de teste.
/// </summary>
public abstract class FabricaBase(string conexao, IDictionary<string, string?>? sobrescritas)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuracao) =>
        {
            configuracao.AddInMemoryCollection(DadosDeTeste.ConfiguracaoBase(conexao));
            if (sobrescritas is not null)
            {
                configuracao.AddInMemoryCollection(sobrescritas);
            }
        });
    }
}

/// <summary>
/// Fábrica padrão (usada como fixture): conexão INUTILIZÁVEL. Testes que não precisam de banco nunca
/// tocam o banco de desenvolvimento — se algum tocar por engano, falha.
/// </summary>
public sealed class JvdApiFactory() : FabricaBase(DadosDeTeste.ConexaoInutilizavel, null);

/// <summary>Fábrica com conexão e/ou configuração específicas do teste.</summary>
public sealed class JvdApiFactoryPersonalizada(
    string? conexao = null,
    IDictionary<string, string?>? sobrescritas = null)
    : FabricaBase(conexao ?? DadosDeTeste.ConexaoInutilizavel, sobrescritas);
