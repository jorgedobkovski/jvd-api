namespace Jvd.Application.Comum;

/// <summary>Erro de regra de negócio. A mensagem nunca deve conter dado pessoal nem a senha informada.</summary>
public sealed record ErroNegocio(string Codigo, string Mensagem);

public class Resultado
{
    public IReadOnlyList<ErroNegocio> Erros { get; protected init; } = [];
    public bool Sucesso => Erros.Count == 0;

    public static Resultado Ok() => new();

    public static Resultado Falha(params ErroNegocio[] erros) => new() { Erros = erros };

    public static Resultado Falha(IEnumerable<ErroNegocio> erros) => new() { Erros = erros.ToList() };
}

public sealed class Resultado<T> : Resultado
{
    public T? Valor { get; private init; }

    public static Resultado<T> Ok(T valor) => new() { Valor = valor };

    public new static Resultado<T> Falha(params ErroNegocio[] erros) => new() { Erros = erros };

    public new static Resultado<T> Falha(IEnumerable<ErroNegocio> erros) => new() { Erros = erros.ToList() };
}

public sealed record PaginaDeResultados<T>(IReadOnlyList<T> Itens, int Pagina, int Tamanho, int Total);

/// <summary>Códigos estáveis de erro de negócio (o app decide a mensagem/ação a partir deles).</summary>
public static class CodigosDeErro
{
    public const string Validacao = "validacao";
    public const string NaoEncontrado = "nao_encontrado";

    /// <summary>Outra alteração simultânea impediu a operação; vale tentar de novo.</summary>
    public const string Conflito = "conflito";

    public const string EmailEmUso = "usuario.email_em_uso";
    public const string SenhaInvalida = "usuario.senha_invalida";
    public const string SenhaAtualIncorreta = "usuario.senha_atual_incorreta";
    public const string NovaSenhaIgualAtual = "usuario.nova_senha_igual_atual";
    public const string UltimoGestor = "usuario.ultimo_gestor";
    public const string NaoPodeAgirEmSi = "usuario.nao_pode_agir_em_si";
    public const string SessaoInvalida = "auth.sessao_invalida";
}
