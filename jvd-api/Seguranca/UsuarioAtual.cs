using System.Security.Claims;
using Jvd.Infrastructure.Autenticacao;
using Microsoft.IdentityModel.JsonWebTokens;

namespace jvd_api.Seguranca;

/// <summary>Lê a identidade do token já validado (nunca de corpo de requisição).</summary>
public static class UsuarioAtual
{
    public static bool TentarObterId(ClaimsPrincipal usuario, out Guid id) =>
        Guid.TryParse(usuario.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out id);

    public static bool DeveTrocarSenha(ClaimsPrincipal usuario) =>
        usuario.HasClaim(NomesDeClaims.TrocarSenha, "true");
}
