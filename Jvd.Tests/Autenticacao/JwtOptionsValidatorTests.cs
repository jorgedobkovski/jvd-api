using Jvd.Infrastructure.Autenticacao;
using Jvd.Tests.Suporte;

namespace Jvd.Tests.Autenticacao;

public class JwtOptionsValidatorTests
{
    private static JwtOptions Valida() => new()
    {
        Issuer = DadosDeTeste.Emissor,
        Audience = DadosDeTeste.Audiencia,
        SigningKey = DadosDeTeste.ChaveJwt,
        AccessTokenMinutos = 30,
        RefreshTokenDias = 7
    };

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validar(JwtOptions o) =>
        new JwtOptionsValidator().Validate(null, o);

    [Fact]
    public void ConfiguracaoCompleta_EhAceita() => Assert.True(Validar(Valida()).Succeeded);

    [Theory]
    [InlineData("")]
    [InlineData("curta")]
    [InlineData("0123456789012345678901234567890")] // 31 bytes
    public void ChaveAusenteOuCurta_EhRejeitada_SemVazarOValor(string chave)
    {
        var opcoes = Valida();
        opcoes.SigningKey = chave;

        var resultado = Validar(opcoes);

        Assert.True(resultado.Failed);
        Assert.Contains("SigningKey", resultado.FailureMessage);
        if (chave.Length > 0)
        {
            Assert.DoesNotContain(chave, resultado.FailureMessage);
        }
    }

    [Fact]
    public void ChaveComExatamente32Bytes_EhAceita()
    {
        var opcoes = Valida();
        opcoes.SigningKey = new string('x', JwtOptions.TamanhoMinimoChave);

        Assert.True(Validar(opcoes).Succeeded);
    }

    [Fact]
    public void IssuerEAudienceSaoObrigatorios()
    {
        var opcoes = Valida();
        opcoes.Issuer = "";
        opcoes.Audience = " ";

        var resultado = Validar(opcoes);

        Assert.True(resultado.Failed);
        Assert.Contains("Jwt:Issuer", resultado.FailureMessage);
        Assert.Contains("Jwt:Audience", resultado.FailureMessage);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]    // 0 desliga a tolerância
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void ToleranciaDoRefresh_PrecisaEstarEntre0e60(int segundos, bool valida)
    {
        var opcoes = Valida();
        opcoes.RefreshToleranciaSegundos = segundos;

        Assert.Equal(valida, Validar(opcoes).Succeeded);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(121, 7)]
    [InlineData(30, 0)]
    [InlineData(30, 31)]
    public void TemposForaDoLimite_SaoRejeitados(int minutos, int dias)
    {
        var opcoes = Valida();
        opcoes.AccessTokenMinutos = minutos;
        opcoes.RefreshTokenDias = dias;

        Assert.True(Validar(opcoes).Failed);
    }
}
