namespace Jvd.Domain;

/// <summary>Números por extenso em português do Brasil, no masculino (para "dias"): 0 a 9.999. Regra pura.</summary>
public static class Extenso
{
    public const int Maximo = 9999;

    private static readonly string[] Unidades =
    [
        "zero", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze",
        "catorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove"
    ];

    private static readonly string[] Dezenas =
        ["", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"];

    private static readonly string[] Centenas =
        ["", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos"];

    public static string Numero(int valor)
    {
        if (valor is < 0 or > Maximo) throw new ArgumentOutOfRangeException(nameof(valor), $"Aceita de 0 a {Maximo}.");
        if (valor < 20) return Unidades[valor];

        if (valor < 100)
        {
            var d = Dezenas[valor / 10];
            return valor % 10 == 0 ? d : $"{d} e {Unidades[valor % 10]}";
        }

        if (valor == 100) return "cem";

        if (valor < 1000)
        {
            var c = Centenas[valor / 100];
            return valor % 100 == 0 ? c : $"{c} e {Numero(valor % 100)}";
        }

        var milhares = valor / 1000;
        var resto = valor % 1000;
        var mil = milhares == 1 ? "mil" : $"{Numero(milhares)} mil";
        if (resto == 0) return mil;

        // "mil e um", "mil e cem", "mil e vinte"; mas "mil duzentos e trinta" (o "e" só entra antes de centenas redondas ou de menos de 100)
        return resto < 100 || resto % 100 == 0 ? $"{mil} e {Numero(resto)}" : $"{mil} {Numero(resto)}";
    }

    /// <summary>Garantia como impressa no talão: <c>30 dias (trinta)</c>, <c>1 dia (um)</c>.</summary>
    public static string Garantia(int dias) => $"{dias} {(dias == 1 ? "dia" : "dias")} ({Numero(dias)})";
}
