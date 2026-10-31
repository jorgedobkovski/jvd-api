using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Jvd.Infrastructure.Persistencia;

/// <summary>
/// Usada só pelas ferramentas do EF (<c>dotnet ef migrations ...</c>), para não precisar subir a API
/// (que exige segredos) só para gerar migrations. Gerar migration NÃO acessa o banco.
/// A conexão pode ser trocada pela variável de ambiente <c>JVD_CONNECTION</c>.
/// </summary>
public sealed class JvdDbContextFactory : IDesignTimeDbContextFactory<JvdDbContext>
{
    private const string ConexaoLocalPadrao =
        "Server=localhost;Database=JvdSistema;Trusted_Connection=True;TrustServerCertificate=True";

    public JvdDbContext CreateDbContext(string[] args)
    {
        var conexao = Environment.GetEnvironmentVariable("JVD_CONNECTION") ?? ConexaoLocalPadrao;
        var opcoes = new DbContextOptionsBuilder<JvdDbContext>().UseSqlServer(conexao).Options;
        return new JvdDbContext(opcoes);
    }
}
