using System.ComponentModel.DataAnnotations;
using Jvd.Application.Clientes;
using Jvd.Application.Comum;
using Jvd.Application.Equipamentos;

namespace jvd_api.Contratos;

/// <summary>
/// Corpo de criação/edição de cliente. Os limites aqui só barram abusos óbvios (payload gigante); as regras de
/// negócio e as mensagens vivem no serviço (Specs/CLIENTES_EQUIPAMENTOS.md).
/// </summary>
public sealed class ClienteRequest
{
    [Required, StringLength(500)] public string NomeRazaoSocial { get; init; } = "";
    [StringLength(500)] public string? NomeFantasia { get; init; }
    [StringLength(40)] public string? Documento { get; init; }
    [StringLength(100)] public string? InscricaoEstadualRg { get; init; }
    [StringLength(500)] public string? Logradouro { get; init; }
    [StringLength(100)] public string? Numero { get; init; }
    [StringLength(300)] public string? Bairro { get; init; }
    [StringLength(40)] public string? Cep { get; init; }
    [StringLength(300)] public string? Cidade { get; init; }
    [StringLength(20)] public string? Uf { get; init; }
    [StringLength(100)] public string? Telefone { get; init; }
    [StringLength(500)] public string? Email { get; init; }

    public DadosDoCliente ParaDados() => new(
        NomeRazaoSocial, NomeFantasia, Documento, InscricaoEstadualRg, Logradouro, Numero, Bairro, Cep, Cidade, Uf, Telefone, Email);
}

public sealed class AtualizarClienteRequest
{
    [Required, StringLength(500)] public string NomeRazaoSocial { get; init; } = "";
    [StringLength(500)] public string? NomeFantasia { get; init; }
    [StringLength(40)] public string? Documento { get; init; }
    [StringLength(100)] public string? InscricaoEstadualRg { get; init; }
    [StringLength(500)] public string? Logradouro { get; init; }
    [StringLength(100)] public string? Numero { get; init; }
    [StringLength(300)] public string? Bairro { get; init; }
    [StringLength(40)] public string? Cep { get; init; }
    [StringLength(300)] public string? Cidade { get; init; }
    [StringLength(20)] public string? Uf { get; init; }
    [StringLength(100)] public string? Telefone { get; init; }
    [StringLength(500)] public string? Email { get; init; }

    /// <summary>Versão do cadastro que a pessoa estava vendo (campo <c>versao</c> das respostas).</summary>
    [Required, StringLength(64)] public string Versao { get; init; } = "";

    public DadosDoCliente ParaDados() => new(
        NomeRazaoSocial, NomeFantasia, Documento, InscricaoEstadualRg, Logradouro, Numero, Bairro, Cep, Cidade, Uf, Telefone, Email);
}

public sealed record AvisoResponse(string Codigo, string Mensagem, IReadOnlyList<Guid> Ids)
{
    public static AvisoResponse De(Aviso a) => new(a.Codigo, a.Mensagem, a.Ids);
}

public sealed record ClienteResponse(
    Guid Id, string NomeRazaoSocial, string? NomeFantasia, string? Documento, string? InscricaoEstadualRg,
    string? Logradouro, string? Numero, string? Bairro, string? Cep, string? Cidade, string? Uf,
    string? Telefone, string? Email, bool CadastroIncompleto, string Origem, bool Ativo,
    DateTime CriadoEm, DateTime AtualizadoEm, string Versao)
{
    public static ClienteResponse De(ClienteResumo c) => new(
        c.Id, c.NomeRazaoSocial, c.NomeFantasia, c.Documento, c.InscricaoEstadualRg, c.Logradouro, c.Numero,
        c.Bairro, c.Cep, c.Cidade, c.Uf, c.Telefone, c.Email, c.CadastroIncompleto, c.Origem.ToString(), c.Ativo,
        DateTime.SpecifyKind(c.CriadoEm, DateTimeKind.Utc), DateTime.SpecifyKind(c.AtualizadoEm, DateTimeKind.Utc), c.Versao);
}

public sealed record ClienteSalvoResponse(ClienteResponse Cliente, IReadOnlyList<AvisoResponse> Avisos)
{
    public static ClienteSalvoResponse De(ClienteSalvo s) =>
        new(ClienteResponse.De(s.Cliente), s.Avisos.Select(AvisoResponse.De).ToList());
}

public sealed class EquipamentoRequest
{
    [Required] public Guid ClienteId { get; init; }
    [Required, StringLength(500)] public string Descricao { get; init; } = "";
    [StringLength(200)] public string? NumeroSerie { get; init; }
    [StringLength(200)] public string? NumeroInmetro { get; init; }
    [StringLength(5000)] public string? Observacoes { get; init; }

    public DadosDoEquipamento ParaDados() => new(ClienteId, Descricao, NumeroSerie, NumeroInmetro, Observacoes);
}

public sealed class AtualizarEquipamentoRequest
{
    [Required] public Guid ClienteId { get; init; }
    [Required, StringLength(500)] public string Descricao { get; init; } = "";
    [StringLength(200)] public string? NumeroSerie { get; init; }
    [StringLength(200)] public string? NumeroInmetro { get; init; }
    [StringLength(5000)] public string? Observacoes { get; init; }
    [Required, StringLength(64)] public string Versao { get; init; } = "";

    public DadosDoEquipamento ParaDados() => new(ClienteId, Descricao, NumeroSerie, NumeroInmetro, Observacoes);
}

public sealed record EquipamentoResponse(
    Guid Id, Guid ClienteId, string ClienteNome, string Descricao, string? NumeroSerie, string? NumeroInmetro,
    string? Observacoes, bool Ativo, DateTime CriadoEm, DateTime AtualizadoEm, string Versao)
{
    public static EquipamentoResponse De(EquipamentoResumo e) => new(
        e.Id, e.ClienteId, e.ClienteNome, e.Descricao, e.NumeroSerie, e.NumeroInmetro, e.Observacoes, e.Ativo,
        DateTime.SpecifyKind(e.CriadoEm, DateTimeKind.Utc), DateTime.SpecifyKind(e.AtualizadoEm, DateTimeKind.Utc), e.Versao);
}

public sealed record EquipamentoSalvoResponse(EquipamentoResponse Equipamento, IReadOnlyList<AvisoResponse> Avisos)
{
    public static EquipamentoSalvoResponse De(EquipamentoSalvo s) =>
        new(EquipamentoResponse.De(s.Equipamento), s.Avisos.Select(AvisoResponse.De).ToList());
}
