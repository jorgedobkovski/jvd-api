using System.ComponentModel.DataAnnotations;
using Jvd.Application.Catalogo;
using Jvd.Application.Emissor;

namespace jvd_api.Contratos;

/// <summary>
/// Corpo do emissor. Os limites daqui só barram payloads abusivos; as regras e mensagens vivem no serviço
/// (Specs/EMISSOR_CATALOGO.md). <c>versao</c> é omitida na primeira configuração e obrigatória depois.
/// </summary>
public sealed class EmissorRequest
{
    [Required, StringLength(500)] public string RazaoSocial { get; init; } = "";
    [StringLength(500)] public string? NomeFantasia { get; init; }
    [Required, StringLength(40)] public string Cnpj { get; init; } = "";
    [StringLength(100)] public string? InscricaoEstadual { get; init; }
    [StringLength(500)] public string? Logradouro { get; init; }
    [StringLength(100)] public string? Numero { get; init; }
    [StringLength(300)] public string? Bairro { get; init; }
    [StringLength(40)] public string? Cep { get; init; }
    [StringLength(300)] public string? Cidade { get; init; }
    [StringLength(20)] public string? Uf { get; init; }
    [StringLength(1000)] public string? Telefones { get; init; }
    [StringLength(300)] public string? NumeroAutorizacaoIpem { get; init; }
    [StringLength(500)] public string? ResponsavelTecnicoNome { get; init; }
    [StringLength(40)] public string? ResponsavelTecnicoCpf { get; init; }
    [StringLength(64)] public string? Versao { get; init; }

    public DadosDoEmissor ParaDados() => new(
        RazaoSocial, NomeFantasia, Cnpj, InscricaoEstadual, Logradouro, Numero, Bairro, Cep, Cidade, Uf, Telefones,
        NumeroAutorizacaoIpem, ResponsavelTecnicoNome, ResponsavelTecnicoCpf);
}

public sealed record EmissorResponse(
    string RazaoSocial, string? NomeFantasia, string Cnpj, string? InscricaoEstadual, string? Logradouro, string? Numero,
    string? Bairro, string? Cep, string? Cidade, string? Uf, string? Telefones, string? NumeroAutorizacaoIpem,
    string? ResponsavelTecnicoNome, string? ResponsavelTecnicoCpf, DateTime AtualizadoEm, string Versao)
{
    public static EmissorResponse De(EmissorResumo e) => new(
        e.RazaoSocial, e.NomeFantasia, e.Cnpj, e.InscricaoEstadual, e.Logradouro, e.Numero, e.Bairro, e.Cep, e.Cidade,
        e.Uf, e.Telefones, e.NumeroAutorizacaoIpem, e.ResponsavelTecnicoNome, e.ResponsavelTecnicoCpf,
        DateTime.SpecifyKind(e.AtualizadoEm, DateTimeKind.Utc), e.Versao);
}

public sealed class ServicoRequest
{
    [Required, StringLength(500)] public string Descricao { get; init; } = "";
    [StringLength(100)] public string? CodigoOrigem { get; init; }
    public decimal? PrecoSugerido { get; init; }

    public DadosDoServico ParaDados() => new(Descricao, CodigoOrigem, PrecoSugerido);
}

public sealed class AtualizarServicoRequest
{
    [Required, StringLength(500)] public string Descricao { get; init; } = "";
    [StringLength(100)] public string? CodigoOrigem { get; init; }
    public decimal? PrecoSugerido { get; init; }

    /// <summary>Versão do cadastro que a pessoa estava vendo (campo <c>versao</c> das respostas).</summary>
    [Required, StringLength(64)] public string Versao { get; init; } = "";

    public DadosDoServico ParaDados() => new(Descricao, CodigoOrigem, PrecoSugerido);
}

public sealed record ServicoResponse(
    Guid Id, string? CodigoOrigem, string Descricao, decimal? PrecoSugerido, bool Ativo,
    DateTime CriadoEm, DateTime AtualizadoEm, string Versao)
{
    public static ServicoResponse De(ServicoResumo s) => new(
        s.Id, s.CodigoOrigem, s.Descricao, s.PrecoSugerido, s.Ativo,
        DateTime.SpecifyKind(s.CriadoEm, DateTimeKind.Utc), DateTime.SpecifyKind(s.AtualizadoEm, DateTimeKind.Utc), s.Versao);
}

public sealed record ServicoSalvoResponse(ServicoResponse Servico, IReadOnlyList<AvisoResponse> Avisos)
{
    public static ServicoSalvoResponse De(ServicoSalvo s) =>
        new(ServicoResponse.De(s.Servico), s.Avisos.Select(AvisoResponse.De).ToList());
}
