using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Tests.Suporte;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Jvd.Tests.Api;

/// <summary>Testes HTTP que NÃO precisam de banco (rodam sempre).</summary>
public class ApiSemBancoTests : IClassFixture<JvdApiFactory>
{
    private readonly JvdApiFactory _fabrica;

    public ApiSemBancoTests(JvdApiFactory fabrica) => _fabrica = fabrica;

    private string TokenValido(PerfilUsuario perfil = PerfilUsuario.Tecnico)
    {
        var emissor = _fabrica.Services.GetRequiredService<EmissorDeTokens>();
        var (token, _) = emissor.GerarAccessToken(new Usuario { Id = Guid.NewGuid(), Nome = "Teste", Perfil = perfil });
        return token;
    }

    private HttpClient ClienteCom(string? token)
    {
        var cliente = _fabrica.CreateClient();
        if (token is not null)
        {
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return cliente;
    }

    [Fact]
    public async Task Saude_Anonimo_Retorna200()
    {
        var resposta = await _fabrica.CreateClient().GetAsync("/api/saude");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Respostas_TrazemCabecalhosDeSeguranca()
    {
        var resposta = await _fabrica.CreateClient().GetAsync("/api/saude");

        Assert.Equal("nosniff", resposta.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", resposta.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", resposta.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Me_SemToken_Retorna401()
    {
        var resposta = await _fabrica.CreateClient().GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Me_ComTokenValido_RetornaIdentidadeDoToken()
    {
        var resposta = await ClienteCom(TokenValido(PerfilUsuario.Gestor)).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.Contains("Gestor", corpo);
        Assert.Contains("Teste", corpo);
    }

    [Fact]
    public async Task Me_ComTokenAdulterado_Retorna401()
    {
        var partes = TokenValido(PerfilUsuario.Tecnico).Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(partes[1])).Replace("Tecnico", "Gestor");
        var adulterado = $"{partes[0]}.{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload))}.{partes[2]}";

        var resposta = await ClienteCom(adulterado).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Me_ComTokenDeOutraChave_Retorna401()
    {
        var emissorEstranho = new EmissorDeTokens(
            Options.Create(new JwtOptions
            {
                Issuer = DadosDeTeste.Emissor,
                Audience = DadosDeTeste.Audiencia,
                SigningKey = "chave-de-um-atacante-0123456789-0123456789-0123456789"
            }),
            TimeProvider.System);
        var (token, _) = emissorEstranho.GerarAccessToken(
            new Usuario { Id = Guid.NewGuid(), Nome = "Atacante", Perfil = PerfilUsuario.Gestor });

        var resposta = await ClienteCom(token).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Me_ComTokenExpirado_Retorna401()
    {
        var emissorNoPassado = new EmissorDeTokens(
            Options.Create(new JwtOptions
            {
                Issuer = DadosDeTeste.Emissor,
                Audience = DadosDeTeste.Audiencia,
                SigningKey = DadosDeTeste.ChaveJwt,
                AccessTokenMinutos = 30
            }),
            new RelogioFalso(DateTimeOffset.UtcNow.AddHours(-2)));
        var (token, _) = emissorNoPassado.GerarAccessToken(
            new Usuario { Id = Guid.NewGuid(), Nome = "Antigo", Perfil = PerfilUsuario.Tecnico });

        var resposta = await ClienteCom(token).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Me_ComTokenDeOutraAudiencia_Retorna401()
    {
        var emissorOutraAudiencia = new EmissorDeTokens(
            Options.Create(new JwtOptions
            {
                Issuer = DadosDeTeste.Emissor,
                Audience = "outro-sistema",
                SigningKey = DadosDeTeste.ChaveJwt
            }),
            TimeProvider.System);
        var (token, _) = emissorOutraAudiencia.GerarAccessToken(
            new Usuario { Id = Guid.NewGuid(), Nome = "Outro", Perfil = PerfilUsuario.Tecnico });

        var resposta = await ClienteCom(token).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Login_ComCorpoInvalido_Retorna400()
    {
        var resposta = await _fabrica.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email = "nao-e-email", senha = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Cors_OrigemPermitida_RecebeOCabecalho()
    {
        var pedido = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        pedido.Headers.Add("Origin", DadosDeTeste.OrigemPermitida);
        pedido.Headers.Add("Access-Control-Request-Method", "POST");
        pedido.Headers.Add("Access-Control-Request-Headers", "content-type");

        var resposta = await _fabrica.CreateClient().SendAsync(pedido);

        Assert.True(resposta.Headers.TryGetValues("Access-Control-Allow-Origin", out var origens));
        Assert.Equal(DadosDeTeste.OrigemPermitida, origens.Single());
    }

    [Fact]
    public async Task Cors_OrigemNaoPermitida_NaoRecebeOCabecalho()
    {
        var pedido = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        pedido.Headers.Add("Origin", "http://site-estranho.invalid");
        pedido.Headers.Add("Access-Control-Request-Method", "POST");

        var resposta = await _fabrica.CreateClient().SendAsync(pedido);

        Assert.False(resposta.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Login_AcimaDoLimiteDeTentativas_Retorna429()
    {
        using var fabrica = new JvdApiFactoryPersonalizada(sobrescritas: new Dictionary<string, string?>
        {
            ["RateLimit:AutenticacaoPorMinuto"] = "3"
        });
        var cliente = fabrica.CreateClient();
        var corpoInvalido = new { email = "x", senha = "" }; // 400 antes de tocar o banco

        var codigos = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            codigos.Add((await cliente.PostAsJsonAsync("/api/auth/login", corpoInvalido)).StatusCode);
        }

        Assert.Equal(3, codigos.Count(c => c == HttpStatusCode.BadRequest));
        Assert.Equal(2, codigos.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Theory]
    [InlineData("")]
    [InlineData("chave-secreta-xyz")] // curta demais
    public void Inicializacao_ComChaveJwtAusenteOuFraca_FalhaSemVazarAChave(string chave)
    {
        using var fabrica = new JvdApiFactoryPersonalizada(sobrescritas: new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = chave
        });

        var erro = Assert.ThrowsAny<Exception>(() => fabrica.CreateClient());

        Assert.Contains("SigningKey", erro.ToString());
        if (chave.Length > 0)
        {
            Assert.DoesNotContain(chave, erro.ToString());
        }
    }
}
