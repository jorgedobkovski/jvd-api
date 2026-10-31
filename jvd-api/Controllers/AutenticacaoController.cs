using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Autenticacao;
using Jvd.Infrastructure.Autenticacao;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace jvd_api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AutenticacaoController(IAutenticacaoService autenticacao) : ControllerBase
{
    public const string PoliticaDeLimite = "autenticacao";

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticaDeLimite)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest pedido, CancellationToken ct)
    {
        var resultado = await autenticacao.LoginAsync(new LoginComando(pedido.Email, pedido.Senha), ct);
        return Responder(resultado);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticaDeLimite)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Renovar([FromBody] RefreshRequest pedido, CancellationToken ct)
    {
        var resultado = await autenticacao.RenovarAsync(pedido.RefreshToken, ct);
        return Responder(resultado);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticaDeLimite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Sair([FromBody] RefreshRequest pedido, CancellationToken ct)
    {
        await autenticacao.EncerrarSessaoAsync(pedido.RefreshToken, ct);
        return NoContent();
    }

    /// <summary>
    /// Troca a PRÓPRIA senha (única ação permitida a quem está com senha provisória — ADR-006).
    /// Revoga todas as sessões e devolve tokens novos.
    /// </summary>
    [HttpPost("trocar-senha")]
    [Authorize(Policy = Politicas.Autenticado)]
    [EnableRateLimiting(PoliticaDeLimite)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TrocarSenha([FromBody] TrocarSenhaRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var usuarioId)) return Unauthorized();

        var resultado = await autenticacao.TrocarSenhaAsync(usuarioId, pedido.SenhaAtual, pedido.NovaSenha, ct);
        if (!resultado.Sucesso) return this.Problema(resultado);

        Response.Headers.CacheControl = "no-store";
        return Ok(TokenResponse.De(resultado.Valor!));
    }

    /// <summary>Quem sou eu? Lê só as claims do token — não consulta o banco. Funciona com senha provisória.</summary>
    [HttpGet("me")]
    [Authorize(Policy = Politicas.Autenticado)]
    [ProducesResponseType<UsuarioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public IActionResult Eu()
    {
        var id = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var nome = User.FindFirst(NomesDeClaims.Nome)?.Value;
        var perfil = User.FindFirst(NomesDeClaims.Perfil)?.Value;

        return Guid.TryParse(id, out var usuarioId) && nome is not null && perfil is not null
            ? Ok(new UsuarioResponse(usuarioId, nome, perfil, UsuarioAtual.DeveTrocarSenha(User)))
            : Unauthorized();
    }

    private IActionResult Responder(ResultadoAutenticacao resultado)
    {
        if (resultado.Sucesso)
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(TokenResponse.De(resultado.Tokens!));
        }

        // Mensagem única e genérica, qualquer que seja o motivo.
        return Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Não autorizado.",
            detail: resultado.Falha == FalhaAutenticacao.TokenInvalido
                ? "Sessão inválida ou expirada."
                : "E-mail ou senha inválidos.");
    }
}
