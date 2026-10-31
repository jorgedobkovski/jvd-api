using Jvd.Application.Comum;
using Jvd.Infrastructure.Autenticacao;
using Microsoft.AspNetCore.Identity;

namespace Jvd.Tests.Autenticacao;

public class TradutorDeErrosIdentityTests
{
    private static IdentityError Erro(string codigo, string descricao = "texto em inglês do Identity") =>
        new() { Code = codigo, Description = descricao };

    [Theory]
    [InlineData("PasswordTooShort", "10 caracteres")]
    [InlineData("PasswordRequiresDigit", "dígito")]
    [InlineData("PasswordRequiresLower", "minúscula")]
    [InlineData("PasswordRequiresUpper", "maiúscula")]
    [InlineData("PasswordRequiresNonAlphanumeric", "especial")]
    public void ErroDeSenha_VirouMensagemFixaEmPortugues(string codigo, string trecho)
    {
        var erro = Assert.Single(TradutorDeErrosIdentity.Senha([Erro(codigo)]));

        Assert.Equal(CodigosDeErro.SenhaInvalida, erro.Codigo);
        Assert.Contains(trecho, erro.Mensagem);
    }

    [Fact]
    public void ErroDeSenhaDesconhecido_TemMensagemGenericaSemEcoarODescritivoOriginal()
    {
        var erro = Assert.Single(TradutorDeErrosIdentity.Senha([Erro("CodigoNovo", "contém a senha hunter2")]));

        Assert.DoesNotContain("hunter2", erro.Mensagem);
        Assert.Contains("política", erro.Mensagem);
    }

    [Theory]
    [InlineData("DuplicateEmail")]
    [InlineData("DuplicateUserName")]
    public void EmailDuplicado_ViraEmailEmUso(string codigo)
    {
        var erro = Assert.Single(TradutorDeErrosIdentity.Usuario([Erro(codigo)]));

        Assert.Equal(CodigosDeErro.EmailEmUso, erro.Codigo);
    }

    [Theory]
    [InlineData("InvalidEmail")]
    [InlineData("InvalidUserName")]
    public void EmailInvalido_ViraValidacao(string codigo) =>
        Assert.Equal(CodigosDeErro.Validacao, Assert.Single(TradutorDeErrosIdentity.Usuario([Erro(codigo)])).Codigo);

    [Fact]
    public void ErroDeSenhaDentroDeUsuario_MantemOCodigoDeSenha() =>
        Assert.Equal(CodigosDeErro.SenhaInvalida,
            Assert.Single(TradutorDeErrosIdentity.Usuario([Erro("PasswordTooShort")])).Codigo);

    [Fact]
    public void ErroDesconhecidoDeUsuario_ViraMensagemGenerica()
    {
        var erro = Assert.Single(TradutorDeErrosIdentity.Usuario([Erro("Qualquer", "detalhe interno")]));

        Assert.Equal(CodigosDeErro.Validacao, erro.Codigo);
        Assert.DoesNotContain("detalhe interno", erro.Mensagem);
    }
}
