using System.Globalization;

namespace Jvd.Application.Comum;

/// <summary>
/// Alerta que NÃO impede a operação (ex.: documento já existente em outro cadastro, que pode ser uma filial).
/// Traz só <b>ids</b> dos registros relacionados — nunca dados pessoais.
/// </summary>
public sealed record Aviso(string Codigo, string Mensagem, IReadOnlyList<Guid> Ids);

/// <summary>
/// Versão de um registro (rowversion do banco) trafegando como texto base64. Quem edita devolve a versão que viu;
/// se o registro mudou nesse meio-tempo, a edição é recusada (409) em vez de sobrescrever a de outra pessoa.
/// </summary>
public static class Versao
{
    public static string Codificar(byte[] bytes) => Convert.ToBase64String(bytes);

    public static bool TentarDecodificar(string? texto, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(texto) || texto.Length > 64)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(texto);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Texto sem valores pessoais para erros de versão.</summary>
    public static string MensagemDeConflito(string cadastro) =>
        string.Create(CultureInfo.InvariantCulture, $"Este {cadastro} foi alterado por outra pessoa. Recarregue e tente de novo.");
}
