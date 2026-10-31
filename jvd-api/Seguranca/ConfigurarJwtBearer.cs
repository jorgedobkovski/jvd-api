using System.Text;
using Jvd.Infrastructure.Autenticacao;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace jvd_api.Seguranca;

/// <summary>
/// Configura a validação do JWT a partir das mesmas <see cref="JwtOptions"/> usadas para emitir o token,
/// garantindo que emissão e validação nunca divirjam.
/// </summary>
public sealed class ConfigurarJwtBearer(IOptions<JwtOptions> opcoes) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        var jwt = opcoes.Value;

        // Sem mapeamento automático de claims: os nomes ("sub", "name", "role") ficam como no token.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = NomesDeClaims.Nome,
            RoleClaimType = NomesDeClaims.Perfil
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);
}
