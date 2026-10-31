using Jvd.Application.Comum;
using Jvd.Domain;

namespace Jvd.Application.Autenticacao;

public sealed record LoginComando(string Email, string Senha);

public sealed record UsuarioAutenticado(Guid Id, string Nome, PerfilUsuario Perfil, bool DeveTrocarSenha);

public sealed record TokensEmitidos(
    string AccessToken,
    DateTime AccessTokenExpiraEm,
    string RefreshToken,
    DateTime RefreshTokenExpiraEm,
    UsuarioAutenticado Usuario);

/// <summary>
/// Motivo genérico de falha. Propositalmente não distingue "usuário inexistente", "senha errada",
/// "conta bloqueada" ou "usuário inativo", para não permitir enumeração de contas.
/// </summary>
public enum FalhaAutenticacao
{
    CredenciaisInvalidas = 1,
    TokenInvalido = 2
}

public sealed record ResultadoAutenticacao
{
    public TokensEmitidos? Tokens { get; private init; }
    public FalhaAutenticacao? Falha { get; private init; }
    public bool Sucesso => Tokens is not null;

    public static ResultadoAutenticacao Ok(TokensEmitidos tokens) => new() { Tokens = tokens };
    public static ResultadoAutenticacao Erro(FalhaAutenticacao falha) => new() { Falha = falha };
}

public interface IAutenticacaoService
{
    Task<ResultadoAutenticacao> LoginAsync(LoginComando comando, CancellationToken ct = default);

    /// <summary>Troca um refresh token válido por um novo par de tokens (rotação, uso único).</summary>
    Task<ResultadoAutenticacao> RenovarAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Revoga o refresh token informado. Idempotente: token desconhecido não gera erro.</summary>
    Task EncerrarSessaoAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Revoga todas as sessões do usuário (troca de senha, desativação, suspeita de roubo).</summary>
    Task RevogarTodasAsync(Guid usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Troca a PRÓPRIA senha. Exige a senha atual (errar conta como tentativa de login e pode bloquear a conta),
    /// aplica a política, revoga todas as sessões e devolve tokens novos já sem a obrigação de troca.
    /// </summary>
    Task<Resultado<TokensEmitidos>> TrocarSenhaAsync(Guid usuarioId, string senhaAtual, string novaSenha, CancellationToken ct = default);
}
