namespace Jvd.Domain;

public enum TipoEventoCadastro
{
    Criado = 1,
    Editado = 2,
    Desativado = 3,
    Reativado = 4,
    SenhaRedefinida = 5,
    SenhaTrocada = 6
}

/// <summary>
/// Trilha de auditoria dos cadastros (quem, quando, o quê). Guarda só <b>nomes de campos</b>, nunca valores:
/// os cadastros contêm dados pessoais (LGPD) e a trilha não pode virar uma segunda cópia deles.
/// </summary>
public sealed class EventoDeCadastro
{
    public Guid Id { get; set; }

    /// <summary>Nome lógico da entidade (ex.: <c>usuario</c>, <c>cliente</c>).</summary>
    public string Entidade { get; set; } = "";

    public Guid EntidadeId { get; set; }
    public TipoEventoCadastro Tipo { get; set; }

    /// <summary>Usuário que fez a ação.</summary>
    public Guid AtorId { get; set; }

    public DateTime OcorridoEm { get; set; }

    /// <summary>Nomes dos campos alterados, separados por vírgula (sem valores).</summary>
    public string? Campos { get; set; }
}
