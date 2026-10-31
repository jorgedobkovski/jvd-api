using System.Text;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Tests.Suporte;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Jvd.Tests.Autenticacao;

public class EmissorDeTokensTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static (EmissorDeTokens Emissor, RelogioFalso Relogio) Criar(int minutos = 30)
    {
        var relogio = new RelogioFalso(Inicio);
        var opcoes = Options.Create(new JwtOptions
        {
            Issuer = DadosDeTeste.Emissor,
            Audience = DadosDeTeste.Audiencia,
            SigningKey = DadosDeTeste.ChaveJwt,
            AccessTokenMinutos = minutos
        });
        return (new EmissorDeTokens(opcoes, relogio), relogio);
    }

    private static Usuario NovoUsuario(PerfilUsuario perfil = PerfilUsuario.Tecnico) => new()
    {
        Id = Guid.NewGuid(),
        Nome = "Fulano de Teste",
        Email = "fulano@exemplo.invalid",
        UserName = "fulano@exemplo.invalid",
        Perfil = perfil
    };

    private static TokenValidationParameters Parametros(string chave = DadosDeTeste.ChaveJwt) => new()
    {
        ValidIssuer = DadosDeTeste.Emissor,
        ValidAudience = DadosDeTeste.Audiencia,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(chave)),
        ValidateLifetime = false
    };

    [Fact]
    public async Task AccessToken_TemSubNomeEPerfil_ESaiValidoComAChaveCerta()
    {
        var (emissor, _) = Criar();
        var usuario = NovoUsuario(PerfilUsuario.Gestor);

        var (token, _) = emissor.GerarAccessToken(usuario);
        var resultado = await new JsonWebTokenHandler().ValidateTokenAsync(token, Parametros());

        Assert.True(resultado.IsValid);
        var jwt = (JsonWebToken)resultado.SecurityToken;
        Assert.Equal(usuario.Id.ToString(), jwt.Subject);
        Assert.Equal("Fulano de Teste", jwt.GetClaim(NomesDeClaims.Nome).Value);
        Assert.Equal("Gestor", jwt.GetClaim(NomesDeClaims.Perfil).Value);
    }

    [Fact]
    public void AccessToken_NaoCarregaEmailNemOutroDadoPessoal()
    {
        var (emissor, _) = Criar();
        var usuario = NovoUsuario();
        usuario.Cpf = "12345678909";

        var (token, _) = emissor.GerarAccessToken(usuario);
        var jwt = new JsonWebToken(token);
        var conteudo = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));

        Assert.DoesNotContain("fulano@exemplo.invalid", conteudo);
        Assert.DoesNotContain("12345678909", conteudo);
        Assert.False(jwt.TryGetClaim("email", out _));
    }

    [Fact]
    public void AccessToken_ExpiraConformeAConfiguracao()
    {
        var (emissor, _) = Criar(minutos: 15);

        var (_, expiraEm) = emissor.GerarAccessToken(NovoUsuario());

        Assert.Equal(Inicio.UtcDateTime.AddMinutes(15), expiraEm);
    }

    [Fact]
    public async Task AccessToken_AssinadoComOutraChave_EhRejeitado()
    {
        var (emissor, _) = Criar();
        var (token, _) = emissor.GerarAccessToken(NovoUsuario());

        var resultado = await new JsonWebTokenHandler().ValidateTokenAsync(
            token, Parametros(chave: "outra-chave-sintetica-totalmente-diferente-0123456789"));

        Assert.False(resultado.IsValid);
    }

    [Fact]
    public async Task AccessToken_Adulterado_EhRejeitado()
    {
        var (emissor, _) = Criar();
        var (token, _) = emissor.GerarAccessToken(NovoUsuario(PerfilUsuario.Tecnico));

        // Troca o payload por um com perfil Gestor, mantendo a assinatura original.
        var partes = token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(partes[1])).Replace("Tecnico", "Gestor");
        var adulterado = $"{partes[0]}.{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload))}.{partes[2]}";

        var resultado = await new JsonWebTokenHandler().ValidateTokenAsync(adulterado, Parametros());

        Assert.False(resultado.IsValid);
    }

    [Fact]
    public void RefreshToken_EhOpacoUnicoEComEntropiaSuficiente()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => EmissorDeTokens.GerarRefreshTokenOpaco()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(t.Length >= 43)); // 32 bytes em base64url = 43 caracteres
        Assert.All(tokens, t => Assert.DoesNotContain('.', t)); // não parece JWT
    }

    [Fact]
    public void Hash_EhDeterministicoMinusculoECom64Caracteres()
    {
        var hash1 = EmissorDeTokens.CalcularHash("valor-qualquer");
        var hash2 = EmissorDeTokens.CalcularHash("valor-qualquer");

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1.ToLowerInvariant(), hash1);
        Assert.NotEqual(hash1, EmissorDeTokens.CalcularHash("valor-diferente"));
    }
}
