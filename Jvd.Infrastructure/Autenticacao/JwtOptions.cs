using System.Text;
using Microsoft.Extensions.Options;

namespace Jvd.Infrastructure.Autenticacao;

/// <summary>
/// Configuração do JWT. <see cref="SigningKey"/> é segredo: vem de user-secrets/variável de ambiente,
/// nunca de arquivo versionado (ADR-004).
/// </summary>
public sealed class JwtOptions
{
    public const string Secao = "Jwt";

    /// <summary>Tamanho mínimo da chave de assinatura (HMAC-SHA256), em bytes.</summary>
    public const int TamanhoMinimoChave = 32;

    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutos { get; set; } = 30;
    public int RefreshTokenDias { get; set; } = 7;

    /// <summary>
    /// Janela (em segundos) em que reapresentar um refresh token JÁ USADO NÃO é tratado como roubo: cobre
    /// pedidos simultâneos legítimos (duas abas, duas chamadas de uma SPA). Dentro da janela o pedido falha
    /// com 401, mas as sessões não são revogadas. 0 desliga a tolerância (qualquer reúso revoga tudo).
    /// </summary>
    public int RefreshToleranciaSegundos { get; set; } = 10;
}

/// <summary>Falha ao iniciar (fail-fast) se a configuração do token estiver ausente ou fraca.</summary>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var erros = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer)) erros.Add("Jwt:Issuer é obrigatório.");
        if (string.IsNullOrWhiteSpace(options.Audience)) erros.Add("Jwt:Audience é obrigatório.");

        if (Encoding.UTF8.GetByteCount(options.SigningKey ?? "") < JwtOptions.TamanhoMinimoChave)
        {
            // Nunca inclui o valor da chave na mensagem.
            erros.Add($"Jwt:SigningKey ausente ou com menos de {JwtOptions.TamanhoMinimoChave} bytes " +
                      "(configure com 'dotnet user-secrets set \"Jwt:SigningKey\" <valor>').");
        }

        if (options.AccessTokenMinutos is < 1 or > 120) erros.Add("Jwt:AccessTokenMinutos deve estar entre 1 e 120.");
        if (options.RefreshTokenDias is < 1 or > 30) erros.Add("Jwt:RefreshTokenDias deve estar entre 1 e 30.");
        if (options.RefreshToleranciaSegundos is < 0 or > 60) erros.Add("Jwt:RefreshToleranciaSegundos deve estar entre 0 e 60.");

        return erros.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(erros);
    }
}
