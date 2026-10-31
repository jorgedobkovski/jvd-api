using Jvd.Application.Comum;
using Jvd.Domain;

namespace Jvd.Application.Usuarios;

public sealed record UsuarioResumo(
    Guid Id,
    string Nome,
    string Email,
    PerfilUsuario Perfil,
    string? Cpf,
    bool Ativo,
    bool DeveTrocarSenha,
    DateTime CriadoEm,
    DateTime AtualizadoEm);

public sealed record FiltroDeUsuarios(string? Busca, PerfilUsuario? Perfil, bool? Ativo, int Pagina = 1, int Tamanho = 20);

public sealed record CriarUsuarioComando(
    string Nome, string Email, PerfilUsuario Perfil, string? Cpf, string SenhaProvisoria, Guid AtorId);

public sealed record AtualizarUsuarioComando(Guid Id, string Nome, PerfilUsuario Perfil, string? Cpf, Guid AtorId);

/// <summary>
/// Gestão de usuários — exclusiva do gestor (Specs/AUTENTICACAO_PERMISSOES.md, ADR-006).
/// Toda operação recebe o <c>AtorId</c> (quem está agindo) para auditoria e para as proteções
/// (não agir em si mesmo, não deixar o sistema sem gestor).
/// </summary>
public interface IUsuarioService
{
    Task<PaginaDeResultados<UsuarioResumo>> ListarAsync(FiltroDeUsuarios filtro, CancellationToken ct = default);

    Task<Resultado<UsuarioResumo>> ObterAsync(Guid id, CancellationToken ct = default);

    Task<Resultado<UsuarioResumo>> CriarAsync(CriarUsuarioComando comando, CancellationToken ct = default);

    Task<Resultado<UsuarioResumo>> AtualizarAsync(AtualizarUsuarioComando comando, CancellationToken ct = default);

    Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default);

    Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default);

    /// <summary>Define nova senha provisória: troca obrigatória, sessões revogadas, bloqueio zerado.</summary>
    Task<Resultado> RedefinirSenhaAsync(Guid id, string novaSenhaProvisoria, Guid atorId, CancellationToken ct = default);
}
