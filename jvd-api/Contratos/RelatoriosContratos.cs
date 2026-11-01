using System.ComponentModel.DataAnnotations;
using Jvd.Application.Relatorios;
using Jvd.Domain;

namespace jvd_api.Contratos;

/// <summary>
/// Corpo do rascunho. Os limites daqui só barram payloads abusivos; as regras e mensagens vivem no serviço
/// (Specs/RELATORIO_SERVICO.md). Status, número, totais calculados e snapshots NÃO existem aqui: a API os controla.
/// </summary>
public sealed class RelatorioRequest
{
    [Required] public Guid ClienteId { get; init; }
    [Required] public Guid EquipamentoId { get; init; }

    /// <summary>Só o gestor pode informar outro executor; omitido = o próprio usuário (ao criar) ou o atual (ao editar).</summary>
    public Guid? TecnicoId { get; init; }

    [Required, StringLength(20)] public string TipoServico { get; init; } = "";
    [Required] public DateOnly? DataServico { get; init; }
    [StringLength(20000)] public string? ServicoExecutado { get; init; }
    [StringLength(20000)] public string? Observacoes { get; init; }
    public DateOnly? VerificacaoAnteriorData { get; init; }
    [StringLength(500)] public string? VerificacaoAnteriorCertificado { get; init; }
    [StringLength(500)] public string? NumeroSeloReparador { get; init; }
    public int GarantiaDias { get; init; }
    public decimal Total { get; init; }
    public decimal Desconto { get; init; }
    public Guid? CatalogoServicoId { get; init; }

    public DadosDoRelatorio ParaDados(TipoServico tipo) => new(
        ClienteId, EquipamentoId, TecnicoId, tipo, DataServico!.Value, ServicoExecutado, Observacoes, VerificacaoAnteriorData,
        VerificacaoAnteriorCertificado, NumeroSeloReparador, GarantiaDias, Total, Desconto, CatalogoServicoId);
}

public sealed class AtualizarRelatorioRequest
{
    [Required] public Guid ClienteId { get; init; }
    [Required] public Guid EquipamentoId { get; init; }
    public Guid? TecnicoId { get; init; }
    [Required, StringLength(20)] public string TipoServico { get; init; } = "";
    [Required] public DateOnly? DataServico { get; init; }
    [StringLength(20000)] public string? ServicoExecutado { get; init; }
    [StringLength(20000)] public string? Observacoes { get; init; }
    public DateOnly? VerificacaoAnteriorData { get; init; }
    [StringLength(500)] public string? VerificacaoAnteriorCertificado { get; init; }
    [StringLength(500)] public string? NumeroSeloReparador { get; init; }
    public int GarantiaDias { get; init; }
    public decimal Total { get; init; }
    public decimal Desconto { get; init; }
    public Guid? CatalogoServicoId { get; init; }

    /// <summary>Versão do relatório que a pessoa estava vendo (campo <c>versao</c> das respostas).</summary>
    [Required, StringLength(64)] public string Versao { get; init; } = "";

    public DadosDoRelatorio ParaDados(TipoServico tipo) => new(
        ClienteId, EquipamentoId, TecnicoId, tipo, DataServico!.Value, ServicoExecutado, Observacoes, VerificacaoAnteriorData,
        VerificacaoAnteriorCertificado, NumeroSeloReparador, GarantiaDias, Total, Desconto, CatalogoServicoId);
}

public sealed class VersaoRequest
{
    [Required, StringLength(64)] public string Versao { get; init; } = "";
}

public sealed record RelatorioResumoResponse(
    Guid Id, int? Numero, string? NumeroFormatado, string Status, string TipoServico, DateOnly DataServico, Guid ClienteId,
    string ClienteNome, Guid EquipamentoId, string EquipamentoDescricao, Guid TecnicoId, string TecnicoNome,
    decimal TotalGeral, int Revisao)
{
    public static RelatorioResumoResponse De(RelatorioResumo r) => new(
        r.Id, r.Numero, r.NumeroFormatado, r.Status.ToString(), r.TipoServico.ToString(), r.DataServico, r.ClienteId,
        r.ClienteNome, r.EquipamentoId, r.EquipamentoDescricao, r.TecnicoId, r.TecnicoNome, r.TotalGeral, r.Revisao);
}

public sealed record RelatorioResponse(
    Guid Id, int? Numero, string? NumeroFormatado, string Status, string TipoServico, DateOnly DataServico, Guid ClienteId,
    string ClienteNome, Guid EquipamentoId, string EquipamentoDescricao, Guid TecnicoId, string TecnicoNome,
    string ServicoExecutado, string? Observacoes, DateOnly? VerificacaoAnteriorData, string? VerificacaoAnteriorCertificado,
    string? NumeroSeloReparador, int GarantiaDias, string GarantiaExtenso, decimal Total, decimal Desconto,
    decimal TotalGeral, Guid? CatalogoServicoId, int Revisao, SnapshotDoCliente? Cliente, SnapshotDoEquipamento? Equipamento,
    SnapshotDoEmissor? Emissor, DateTime? EmitidoEm, Guid? EmitidoPor, DateTime CriadoEm, DateTime AtualizadoEm, string Versao)
{
    public static RelatorioResponse De(RelatorioDetalhe r) => new(
        r.Id, r.Numero, r.NumeroFormatado, r.Status.ToString(), r.TipoServico.ToString(), r.DataServico, r.ClienteId,
        r.ClienteNome, r.EquipamentoId, r.EquipamentoDescricao, r.TecnicoId, r.TecnicoNome, r.ServicoExecutado,
        r.Observacoes, r.VerificacaoAnteriorData, r.VerificacaoAnteriorCertificado, r.NumeroSeloReparador, r.GarantiaDias,
        r.GarantiaExtenso, r.Total, r.Desconto, r.TotalGeral, r.CatalogoServicoId, r.Revisao, r.Cliente, r.Equipamento,
        r.Emissor, Utc(r.EmitidoEm), r.EmitidoPor, DateTime.SpecifyKind(r.CriadoEm, DateTimeKind.Utc),
        DateTime.SpecifyKind(r.AtualizadoEm, DateTimeKind.Utc), r.Versao);

    private static DateTime? Utc(DateTime? valor) => valor is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}

public static class Enumeracoes
{
    /// <summary>Aceita só o NOME (nunca o número), ignorando maiúsculas.</summary>
    public static bool TentarConverter<TEnum>(string? texto, out TEnum valor) where TEnum : struct, Enum
    {
        valor = default;
        return !string.IsNullOrWhiteSpace(texto)
               && Enum.GetNames<TEnum>().Any(n => string.Equals(n, texto.Trim(), StringComparison.OrdinalIgnoreCase))
               && Enum.TryParse(texto.Trim(), ignoreCase: true, out valor);
    }
}
