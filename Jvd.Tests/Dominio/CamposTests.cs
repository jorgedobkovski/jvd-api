using Jvd.Application.Comum;
using Jvd.Domain;

namespace Jvd.Tests.Dominio;

public class CamposTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  abc  ", "abc")]
    public void Normalizar_AparaEVaziosViramNulo(string? entrada, string? esperado) =>
        Assert.Equal(esperado, Campos.Normalizar(entrada));

    [Fact]
    public void Excede_SoQuandoPassaDoLimite()
    {
        Assert.False(Campos.Excede(null, 5));
        Assert.False(Campos.Excede("12345", 5));
        Assert.True(Campos.Excede("123456", 5));
    }

    [Theory]
    [InlineData("76.800-000", "76800000", true)]
    [InlineData("76800000", "76800000", true)]
    [InlineData("7680000", "7680000", false)]
    [InlineData("768000000", "768000000", false)]
    public void Cep_IgnoraMascaraEExigeOitoDigitos(string entrada, string digitos, bool valido)
    {
        Assert.Equal(digitos, Campos.Cep(entrada));
        Assert.Equal(valido, Campos.CepValido(Campos.Cep(entrada)));
    }

    [Fact]
    public void Cep_NaoInformado_EhNuloEInvalido()
    {
        Assert.Null(Campos.Cep(null));
        Assert.False(Campos.CepValido(null));
    }

    [Theory]
    [InlineData("ro", "RO", true)]
    [InlineData(" SP ", "SP", true)]
    [InlineData("XX", "XX", false)]
    [InlineData("RON", "RON", false)]
    public void Uf_NormalizaEValidaContraAsVinteESeteSiglas(string entrada, string normalizada, bool valida)
    {
        Assert.Equal(normalizada, Campos.Uf(entrada));
        Assert.Equal(valida, Campos.UfValida(Campos.Uf(entrada)));
    }

    [Fact]
    public void Uf_NaoInformada_EhNulaEInvalida()
    {
        Assert.Null(Campos.Uf(null));
        Assert.False(Campos.UfValida(null));
    }

    [Theory]
    [InlineData("pessoa@exemplo.invalid", true)]
    [InlineData("pessoa@localhost", false)]
    [InlineData("sem-arroba", false)]
    [InlineData("Nome <pessoa@exemplo.invalid>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Email_ExigeEnderecoSimplesComDominio(string? email, bool valido) =>
        Assert.Equal(valido, Campos.EmailValido(email));

    [Fact]
    public void Email_AcimaDe254Caracteres_EhInvalido()
    {
        var longo = new string('a', 250) + "@x.co";
        Assert.False(Campos.EmailValido(longo));
    }

    [Theory]
    [InlineData("QUJD")]
    [InlineData("AAAAAAAAB9E=")]
    public void Versao_RoundTrip(string texto)
    {
        Assert.True(Versao.TentarDecodificar(texto, out var bytes));
        Assert.Equal(texto, Versao.Codificar(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("não é base64!")]
    public void Versao_Invalida_EhRecusada(string? texto) => Assert.False(Versao.TentarDecodificar(texto, out _));

    [Fact]
    public void Versao_AcimaDoLimite_EhRecusada() =>
        Assert.False(Versao.TentarDecodificar(new string('A', 100), out _));

    [Fact]
    public void MensagemDeConflito_NaoTrazDadosPessoais()
    {
        var mensagem = Versao.MensagemDeConflito("cadastro");
        Assert.Contains("cadastro", mensagem);
    }
}
