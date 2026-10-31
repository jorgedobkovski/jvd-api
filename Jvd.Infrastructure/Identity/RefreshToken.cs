namespace Jvd.Infrastructure.Identity;

/// <summary>
/// Refresh token de uso único (rotativo). Só o hash SHA-256 é gravado; o valor em claro existe
/// apenas na resposta de login/renovação.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CriadoEm { get; set; }
    public DateTime ExpiraEm { get; set; }

    /// <summary>Preenchido quando o token foi trocado por um novo (rotação).</summary>
    public DateTime? UsadoEm { get; set; }

    /// <summary>Preenchido em logout, troca de senha ou detecção de reúso.</summary>
    public DateTime? RevogadoEm { get; set; }

    public Guid? SubstituidoPorId { get; set; }
}
