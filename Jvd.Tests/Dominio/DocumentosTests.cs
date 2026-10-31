using Jvd.Domain;

namespace Jvd.Tests.Dominio;

public class DocumentosTests
{
    // CPFs e CNPJs SINTÉTICOS com dígitos verificadores corretos (nenhum é de pessoa ou empresa real).
    [Theory]
    [InlineData("52998224725")]
    [InlineData("529.982.247-25")]
    [InlineData("  529 982 247 25  ")]
    public void Cpf_Valido_EAceitoComOuSemMascara(string cpf) => Assert.True(Documentos.CpfValido(cpf));

    [Theory]
    [InlineData("52998224724")]   // dígito verificador errado
    [InlineData("11111111111")]   // todos iguais
    [InlineData("00000000000")]
    [InlineData("5299822472")]    // 10 dígitos
    [InlineData("529982247255")]  // 12 dígitos
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Cpf_Invalido_EhRejeitado(string? cpf) => Assert.False(Documentos.CpfValido(cpf));

    [Theory]
    [InlineData("11222333000181")]
    [InlineData("11.222.333/0001-81")]
    public void Cnpj_Valido_EAceitoComOuSemMascara(string cnpj) => Assert.True(Documentos.CnpjValido(cnpj));

    [Theory]
    [InlineData("11222333000182")]  // dígito verificador errado
    [InlineData("11111111111111")]
    [InlineData("1122233300018")]   // 13 dígitos
    [InlineData("")]
    [InlineData(null)]
    public void Cnpj_Invalido_EhRejeitado(string? cnpj) => Assert.False(Documentos.CnpjValido(cnpj));

    [Fact]
    public void CpfOuCnpj_AceitaOsDoisEsomenteOsDois()
    {
        Assert.True(Documentos.CpfOuCnpjValido("52998224725"));
        Assert.True(Documentos.CpfOuCnpjValido("11222333000181"));
        Assert.False(Documentos.CpfOuCnpjValido("12345678901"));
        Assert.False(Documentos.CpfOuCnpjValido(null));
    }

    [Theory]
    [InlineData("529.982.247-25", "52998224725")]
    [InlineData("  (69) 99999-0000 ", "69999990000")]
    [InlineData("abc", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void SomenteDigitos_RemoveTudoQueNaoEDigito(string? entrada, string? esperado) =>
        Assert.Equal(esperado, Documentos.SomenteDigitos(entrada));
}
