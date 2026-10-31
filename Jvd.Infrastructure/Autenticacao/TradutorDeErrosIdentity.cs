using Jvd.Application.Comum;
using Microsoft.AspNetCore.Identity;

namespace Jvd.Infrastructure.Autenticacao;

/// <summary>
/// Converte os erros do Identity (em inglês, às vezes com texto genérico) em erros de negócio em português.
/// As mensagens são fixas: nunca incluem a senha, o e-mail nem qualquer valor informado.
/// </summary>
public static class TradutorDeErrosIdentity
{
    public static IReadOnlyList<ErroNegocio> Senha(IEnumerable<IdentityError> erros) =>
        erros.Select(e => new ErroNegocio(CodigosDeErro.SenhaInvalida, MensagemDaSenha(e.Code))).ToList();

    public static IReadOnlyList<ErroNegocio> Usuario(IEnumerable<IdentityError> erros) =>
        erros.Select(Traduzir).ToList();

    private static ErroNegocio Traduzir(IdentityError erro) => erro.Code switch
    {
        "DuplicateEmail" or "DuplicateUserName" =>
            new ErroNegocio(CodigosDeErro.EmailEmUso, "Já existe um usuário com este e-mail."),
        "InvalidEmail" or "InvalidUserName" =>
            new ErroNegocio(CodigosDeErro.Validacao, "E-mail inválido."),
        _ when erro.Code.StartsWith("Password", StringComparison.Ordinal) =>
            new ErroNegocio(CodigosDeErro.SenhaInvalida, MensagemDaSenha(erro.Code)),
        _ => new ErroNegocio(CodigosDeErro.Validacao, "Não foi possível salvar o usuário.")
    };

    private static string MensagemDaSenha(string codigo) => codigo switch
    {
        "PasswordTooShort" => "A senha precisa ter pelo menos 10 caracteres.",
        "PasswordRequiresDigit" => "A senha precisa ter pelo menos um dígito.",
        "PasswordRequiresLower" => "A senha precisa ter pelo menos uma letra minúscula.",
        "PasswordRequiresUpper" => "A senha precisa ter pelo menos uma letra maiúscula.",
        "PasswordRequiresNonAlphanumeric" => "A senha precisa ter pelo menos um caractere especial.",
        "PasswordRequiresUniqueChars" => "A senha precisa ter mais caracteres diferentes entre si.",
        _ => "A senha não atende à política de segurança."
    };
}
