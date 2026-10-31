using System.ComponentModel.DataAnnotations;
using Jvd.Application.Autenticacao;

namespace jvd_api.Contratos;

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = "";

    [Required, StringLength(128)]
    public string Senha { get; init; } = "";
}

public sealed class RefreshRequest
{
    [Required, StringLength(200)]
    public string RefreshToken { get; init; } = "";
}

public sealed record UsuarioResponse(Guid Id, string Nome, string Perfil);

public sealed record TokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiraEm,
    string RefreshToken,
    DateTime RefreshTokenExpiraEm,
    UsuarioResponse Usuario)
{
    public static TokenResponse De(TokensEmitidos t) => new(
        t.AccessToken,
        DateTime.SpecifyKind(t.AccessTokenExpiraEm, DateTimeKind.Utc),
        t.RefreshToken,
        DateTime.SpecifyKind(t.RefreshTokenExpiraEm, DateTimeKind.Utc),
        new UsuarioResponse(t.Usuario.Id, t.Usuario.Nome, t.Usuario.Perfil.ToString()));
}
