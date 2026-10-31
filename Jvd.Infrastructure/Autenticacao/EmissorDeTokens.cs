using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jvd.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Jvd.Infrastructure.Autenticacao;

/// <summary>Gera o access token (JWT) e os refresh tokens opacos.</summary>
public sealed class EmissorDeTokens(IOptions<JwtOptions> opcoes, TimeProvider relogio)
{
    private readonly JwtOptions _jwt = opcoes.Value;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>
    /// Claims do token: apenas id (<c>sub</c>), nome e perfil. Não leva e-mail, CPF nem outro dado pessoal.
    /// </summary>
    public (string Token, DateTime ExpiraEm) GerarAccessToken(Usuario usuario)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        var expira = agora.AddMinutes(_jwt.AccessTokenMinutos);

        var descritor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = agora,
            NotBefore = agora,
            Expires = expira,
            Subject = new ClaimsIdentity(Claims(usuario)),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };

        return (_handler.CreateToken(descritor), expira);
    }

    private static List<Claim> Claims(Usuario usuario)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(NomesDeClaims.Nome, usuario.Nome),
            new(NomesDeClaims.Perfil, usuario.Perfil.ToString())
        };

        // Senha provisória: o token só serve para trocar a senha (política em jvd-api/Seguranca/Politicas.cs).
        if (usuario.DeveTrocarSenha)
        {
            claims.Add(new Claim(NomesDeClaims.TrocarSenha, "true"));
        }

        return claims;
    }

    /// <summary>Token opaco de 256 bits, em base64url. O valor em claro nunca é gravado.</summary>
    public static string GerarRefreshTokenOpaco() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Hash SHA-256 em hexadecimal minúsculo (64 caracteres) — é o que vai ao banco.</summary>
    public static string CalcularHash(string tokenEmClaro) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokenEmClaro)));
}

/// <summary>Nomes das claims do JWT do JVD (<c>role</c> é a claim usada pela autorização por perfil).</summary>
public static class NomesDeClaims
{
    public const string Nome = "name";
    public const string Perfil = "role";
    public const string TrocarSenha = "trocar_senha";
}
