namespace Jvd.Domain;

/// <summary>Normalização e validação de CPF e CNPJ (dígitos verificadores). Regra pura, sem dependências.</summary>
public static class Documentos
{
    /// <summary>Remove tudo que não é dígito. <c>null</c> ou vazio vira <c>null</c>.</summary>
    public static string? SomenteDigitos(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return null;
        }

        var digitos = new string(valor.Where(char.IsAsciiDigit).ToArray());
        return digitos.Length == 0 ? null : digitos;
    }

    public static bool CpfValido(string? valor)
    {
        var d = SomenteDigitos(valor);
        if (d is null || d.Length != 11 || TodosIguais(d))
        {
            return false;
        }

        return DigitoCpf(d, 9) == d[9] - '0' && DigitoCpf(d, 10) == d[10] - '0';
    }

    public static bool CnpjValido(string? valor)
    {
        var d = SomenteDigitos(valor);
        if (d is null || d.Length != 14 || TodosIguais(d))
        {
            return false;
        }

        int[] pesos1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] pesos2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        return DigitoCnpj(d, 12, pesos1) == d[12] - '0' && DigitoCnpj(d, 13, pesos2) == d[13] - '0';
    }

    /// <summary>CPF (11 dígitos) ou CNPJ (14 dígitos) válido.</summary>
    public static bool CpfOuCnpjValido(string? valor) => CpfValido(valor) || CnpjValido(valor);

    private static bool TodosIguais(string d) => d.All(c => c == d[0]);

    private static int DigitoCpf(string d, int quantidade)
    {
        var soma = 0;
        for (var i = 0; i < quantidade; i++)
        {
            soma += (d[i] - '0') * (quantidade + 1 - i);
        }

        var resto = soma * 10 % 11;
        return resto == 10 ? 0 : resto;
    }

    private static int DigitoCnpj(string d, int quantidade, int[] pesos)
    {
        var soma = 0;
        for (var i = 0; i < quantidade; i++)
        {
            soma += (d[i] - '0') * pesos[i];
        }

        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }
}
