namespace Jvd.Domain;

public enum OrigemCadastro
{
    Manual = 1,
    Importado = 2
}

/// <summary>
/// Cliente atendido pela empresa (Specs/CLIENTES_EQUIPAMENTOS.md). CPF/CNPJ <b>não é único</b>: o mesmo documento
/// pode existir em filiais/locais diferentes. Dado pessoal (LGPD): nunca em log.
/// </summary>
public sealed class Cliente
{
    public Guid Id { get; set; }
    public string NomeRazaoSocial { get; set; } = "";
    public string? NomeFantasia { get; set; }

    /// <summary>Só dígitos (CPF 11 ou CNPJ 14), já validado. <c>null</c> = cadastro incompleto.</summary>
    public string? Documento { get; set; }

    public string? InscricaoEstadualRg { get; set; }
    public string? Logradouro { get; set; }
    public string? Numero { get; set; }
    public string? Bairro { get; set; }
    public string? Cep { get; set; }
    public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public string? Telefone { get; set; }
    public string? Email { get; set; }

    /// <summary>Calculado: verdadeiro enquanto não houver CPF/CNPJ.</summary>
    public bool CadastroIncompleto { get; set; }

    public OrigemCadastro Origem { get; set; } = OrigemCadastro.Manual;
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    /// <summary>Token de concorrência (rowversion): impede sobrescrever em silêncio uma edição de outra pessoa.</summary>
    public byte[] Versao { get; set; } = [];
}

/// <summary>Balança de um cliente. Número de série e do INMETRO <b>não são únicos</b> (a mesma balança é reparada várias vezes).</summary>
public sealed class Equipamento
{
    public Guid Id { get; set; }
    public Guid ClienteId { get; set; }
    public string Descricao { get; set; } = "";
    public string? NumeroSerie { get; set; }
    public string? NumeroInmetro { get; set; }
    public string? Observacoes { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }
    public byte[] Versao { get; set; } = [];
}
