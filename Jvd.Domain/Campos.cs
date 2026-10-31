using System.Net.Mail;

namespace Jvd.Domain;

/// <summary>Normalização e validação de campos de cadastro. Regra pura, sem dependências.</summary>
public static class Campos
{
    private static readonly HashSet<string> Ufs = new(StringComparer.Ordinal)
    {
        "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA",
        "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"
    };

    /// <summary>Apara os espaços; vazio ou só espaços vira <c>null</c>.</summary>
    public static string? Normalizar(string? valor)
    {
        var aparado = valor?.Trim();
        return string.IsNullOrEmpty(aparado) ? null : aparado;
    }

    public static bool Excede(string? valor, int maximo) => valor is not null && valor.Length > maximo;

    /// <summary>CEP brasileiro: exatamente 8 dígitos (a máscara é ignorada). <c>null</c> se não informado.</summary>
    public static string? Cep(string? valor) => Documentos.SomenteDigitos(valor);

    public static bool CepValido(string? digitos) => digitos is { Length: 8 };

    /// <summary>Sigla de UF em maiúsculas; <c>null</c> se não informada.</summary>
    public static string? Uf(string? valor) => Normalizar(valor)?.ToUpperInvariant();

    public static bool UfValida(string? uf) => uf is not null && Ufs.Contains(uf);

    public static bool EmailValido(string? email) =>
        email is { Length: > 0 and <= 254 }
        && MailAddress.TryCreate(email, out var endereco)
        && endereco.Address == email
        && endereco.Host.Contains('.');
}
