using System.ComponentModel.DataAnnotations;
using Jvd.Application.Comum;
using Jvd.Application.Usuarios;
using Jvd.Domain;

namespace jvd_api.Contratos;

public sealed class CriarUsuarioRequest
{
    [Required, StringLength(150)] public string Nome { get; init; } = "";
    [Required, StringLength(254)] public string Email { get; init; } = "";
    [Required, StringLength(20)] public string Perfil { get; init; } = "";
    [StringLength(20)] public string? Cpf { get; init; }
    [Required, StringLength(128)] public string SenhaProvisoria { get; init; } = "";
}

public sealed class AtualizarUsuarioRequest
{
    [Required, StringLength(150)] public string Nome { get; init; } = "";
    [Required, StringLength(20)] public string Perfil { get; init; } = "";
    [StringLength(20)] public string? Cpf { get; init; }
}

public sealed class RedefinirSenhaRequest
{
    [Required, StringLength(128)] public string NovaSenhaProvisoria { get; init; } = "";
}

public sealed class TrocarSenhaRequest
{
    [Required, StringLength(128)] public string SenhaAtual { get; init; } = "";
    [Required, StringLength(128)] public string NovaSenha { get; init; } = "";
}

public sealed record UsuarioDetalheResponse(
    Guid Id, string Nome, string Email, string Perfil, string? Cpf,
    bool Ativo, bool DeveTrocarSenha, DateTime CriadoEm, DateTime AtualizadoEm)
{
    public static UsuarioDetalheResponse De(UsuarioResumo u) => new(
        u.Id, u.Nome, u.Email, u.Perfil.ToString(), u.Cpf, u.Ativo, u.DeveTrocarSenha,
        DateTime.SpecifyKind(u.CriadoEm, DateTimeKind.Utc), DateTime.SpecifyKind(u.AtualizadoEm, DateTimeKind.Utc));
}

public sealed record PaginaResponse<T>(IReadOnlyList<T> Itens, int Pagina, int Tamanho, int Total)
{
    public static PaginaResponse<TSaida> De<TEntrada, TSaida>(PaginaDeResultados<TEntrada> pagina, Func<TEntrada, TSaida> mapa) =>
        new(pagina.Itens.Select(mapa).ToList(), pagina.Pagina, pagina.Tamanho, pagina.Total);
}

public static class Perfis
{
    /// <summary>Aceita só o NOME do perfil (nunca o número), ignorando maiúsculas.</summary>
    public static bool TentarConverter(string? texto, out PerfilUsuario perfil)
    {
        perfil = default;
        return !string.IsNullOrWhiteSpace(texto)
               && Enum.GetNames<PerfilUsuario>().Any(n => string.Equals(n, texto.Trim(), StringComparison.OrdinalIgnoreCase))
               && Enum.TryParse(texto.Trim(), ignoreCase: true, out perfil);
    }
}
