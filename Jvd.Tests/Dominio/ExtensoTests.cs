using Jvd.Domain;

namespace Jvd.Tests.Dominio;

public class ExtensoTests
{
    [Theory]
    [InlineData(0, "zero")]
    [InlineData(1, "um")]
    [InlineData(2, "dois")]
    [InlineData(3, "três")]
    [InlineData(10, "dez")]
    [InlineData(11, "onze")]
    [InlineData(15, "quinze")]
    [InlineData(16, "dezesseis")]
    [InlineData(19, "dezenove")]
    [InlineData(20, "vinte")]
    [InlineData(21, "vinte e um")]
    [InlineData(30, "trinta")]
    [InlineData(45, "quarenta e cinco")]
    [InlineData(90, "noventa")]
    [InlineData(99, "noventa e nove")]
    [InlineData(100, "cem")]
    [InlineData(101, "cento e um")]
    [InlineData(110, "cento e dez")]
    [InlineData(200, "duzentos")]
    [InlineData(250, "duzentos e cinquenta")]
    [InlineData(365, "trezentos e sessenta e cinco")]
    [InlineData(999, "novecentos e noventa e nove")]
    [InlineData(1000, "mil")]
    [InlineData(1001, "mil e um")]
    [InlineData(1020, "mil e vinte")]
    [InlineData(1100, "mil e cem")]
    [InlineData(1101, "mil cento e um")]
    [InlineData(1500, "mil e quinhentos")]
    [InlineData(1999, "mil novecentos e noventa e nove")]
    [InlineData(2000, "dois mil")]
    [InlineData(2001, "dois mil e um")]
    [InlineData(2500, "dois mil e quinhentos")]
    [InlineData(3650, "três mil seiscentos e cinquenta")]
    [InlineData(9999, "nove mil novecentos e noventa e nove")]
    public void Numero_PorExtenso(int valor, string esperado) => Assert.Equal(esperado, Extenso.Numero(valor));

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public void Numero_ForaDoIntervalo_Falha(int valor) => Assert.Throws<ArgumentOutOfRangeException>(() => Extenso.Numero(valor));

    [Theory]
    [InlineData(30, "30 dias (trinta)")]
    [InlineData(1, "1 dia (um)")]
    [InlineData(0, "0 dias (zero)")]
    [InlineData(90, "90 dias (noventa)")]
    [InlineData(365, "365 dias (trezentos e sessenta e cinco)")]
    public void Garantia_NoFormatoDoTalao(int dias, string esperado) => Assert.Equal(esperado, Extenso.Garantia(dias));

    [Fact]
    public void TodosOsNumerosDe0a9999_TemTextoNaoVazio_SemEspacosDuplos()
    {
        for (var i = 0; i <= Extenso.Maximo; i++)
        {
            var texto = Extenso.Numero(i);
            Assert.False(string.IsNullOrWhiteSpace(texto));
            Assert.DoesNotContain("  ", texto);
            Assert.Equal(texto.Trim(), texto);
        }
    }
}
