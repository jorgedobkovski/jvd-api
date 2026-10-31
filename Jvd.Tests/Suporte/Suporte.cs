using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace Jvd.Tests.Suporte;

/// <summary>
/// Teste que usa o SQL Server local. Fica DESLIGADO por padrão: exige <c>JVD_TESTES_BANCO=1</c> porque
/// cria e apaga bancos descartáveis <c>JvdSistema_Test_*</c> (autorização do Owner, §17.4).
/// </summary>
public sealed class FactBancoAttribute : FactAttribute
{
    public const string Variavel = "JVD_TESTES_BANCO";

    public FactBancoAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variavel) != "1")
        {
            Skip = $"Teste de banco desligado. Defina {Variavel}=1 para rodar (cria/apaga bancos JvdSistema_Test_*).";
        }
    }
}

/// <summary>Relógio controlável, para testar expiração sem esperar.</summary>
public sealed class RelogioFalso(DateTimeOffset inicio) : TimeProvider
{
    private DateTimeOffset _agora = inicio;

    public override DateTimeOffset GetUtcNow() => _agora;

    public void Avancar(TimeSpan intervalo) => _agora += intervalo;
}

public static class DadosDeTeste
{
    /// <summary>Valores SINTÉTICOS. Nada aqui é segredo real nem dado de cliente.</summary>
    public const string ChaveJwt = "chave-sintetica-apenas-para-testes-0123456789-abcdefghij";
    public const string Emissor = "jvd-api-teste";
    public const string Audiencia = "jvd-app-teste";
    public const string SenhaValida = "SenhaDeTeste#2026";
    public const string OrigemPermitida = "http://localhost:4200";

    /// <summary>CPF sintético VÁLIDO e único (dígitos verificadores calculados), para testes que dependem de documento exclusivo.</summary>
    public static string NovoCpf()
    {
        var d = new int[11];
        var acaso = Random.Shared;
        do
        {
            for (var i = 0; i < 9; i++) d[i] = acaso.Next(10);
        } while (d.Take(9).Distinct().Count() == 1);

        for (var alvo = 9; alvo < 11; alvo++)
        {
            var soma = 0;
            for (var i = 0; i < alvo; i++) soma += d[i] * (alvo + 1 - i);
            var resto = soma * 10 % 11;
            d[alvo] = resto == 10 ? 0 : resto;
        }

        return string.Concat(d);
    }

    public static string NovoEmail() => $"teste-{Guid.NewGuid():N}@exemplo.invalid";

    public static Dictionary<string, string?> ConfiguracaoBase(string conexao) => new()
    {
        ["ConnectionStrings:JvdSistema"] = conexao,
        ["Jwt:Issuer"] = Emissor,
        ["Jwt:Audience"] = Audiencia,
        ["Jwt:SigningKey"] = ChaveJwt,
        ["Jwt:AccessTokenMinutos"] = "30",
        ["Jwt:RefreshTokenDias"] = "7",
        ["Jwt:RefreshToleranciaSegundos"] = "10",
        ["Cors:AllowedOrigins:0"] = OrigemPermitida,
        ["Seed:Habilitado"] = "false"
    };

    /// <summary>
    /// Conexão que NUNCA é válida: usada pelos testes que não deveriam tocar o banco. Se algum tocar
    /// por engano, falha em vez de acessar o banco de desenvolvimento.
    /// </summary>
    public const string ConexaoInutilizavel =
        "Server=localhost;Database=JvdSistema_Test_NUNCA_USAR;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=2";
}

/// <summary>
/// Banco descartável para um teste/classe: nome único, criado pelas migrations (o que também testa as
/// migrations) e apagado ao final. Só apaga bancos com o prefixo de teste.
/// </summary>
public sealed class BancoDeTeste : IAsyncLifetime, IAsyncDisposable
{
    private const string Prefixo = "JvdSistema_Test_";

    public string Nome { get; } = Prefixo + Guid.NewGuid().ToString("N")[..12];

    public string Conexao =>
        $"Server=localhost;Database={Nome};Trusted_Connection=True;TrustServerCertificate=True";

    public async Task InitializeAsync()
    {
        await using var db = CriarContexto();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!Nome.StartsWith(Prefixo, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recusado: só bancos descartáveis de teste podem ser apagados.");
        }

        await using var db = CriarContexto();
        await db.Database.EnsureDeletedAsync();
    }

    // Permite `await using` nos testes que criam o próprio banco (apagar duas vezes é inofensivo).
    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    public JvdDbContext CriarContexto() =>
        new(new DbContextOptionsBuilder<JvdDbContext>().UseSqlServer(Conexao).Options);

    public static async Task<BancoDeTeste> CriarAsync()
    {
        var banco = new BancoDeTeste();
        await banco.InitializeAsync();
        return banco;
    }
}
