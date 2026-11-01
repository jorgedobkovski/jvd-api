using Jvd.Application.Comum;
using Jvd.Domain;

namespace Jvd.Application.Relatorios;

// ---------- indicadores (Fase 8B; só o gestor vê valores — Specs/AUTENTICACAO_PERMISSOES.md) ----------

/// <summary>Totais do período. Só relatórios EMITIDOS (cancelados e rascunhos ficam de fora). Retificados contam pelos valores vigentes.</summary>
public sealed record TotaisDoPeriodo(int Quantidade, decimal Total, decimal Descontos, decimal TotalGeral, decimal TicketMedio);

public sealed record IndicadorPorExecutor(Guid TecnicoId, string TecnicoNome, int Quantidade, decimal TotalGeral);

public sealed record IndicadorPorTipo(TipoServico Tipo, int Quantidade, decimal TotalGeral);

public sealed record IndicadorPorCliente(Guid ClienteId, string ClienteNome, int Quantidade, decimal TotalGeral);

/// <summary>
/// Indicadores gerenciais do período (data do serviço). <c>De</c>/<c>Ate</c> nulos = sem limite. Os rankings vêm
/// ordenados pelo valor, do maior para o menor.
/// </summary>
public sealed record Indicadores(
    DateOnly? De,
    DateOnly? Ate,
    TotaisDoPeriodo Totais,
    IReadOnlyList<IndicadorPorExecutor> PorExecutor,
    IReadOnlyList<IndicadorPorTipo> PorTipo,
    IReadOnlyList<IndicadorPorCliente> MaioresClientes);

public interface IIndicadoresService
{
    Task<Resultado<Indicadores>> ObterAsync(DateOnly? de, DateOnly? ate, CancellationToken ct = default);
}

// ---------- exportação para planilha ----------

/// <summary>Filtros da exportação (status é sempre Emitido). Mesmos critérios da listagem de relatórios.</summary>
public sealed record FiltroDeExportacao(DateOnly? De, DateOnly? Ate, Guid? ClienteId, Guid? EquipamentoId, Guid? TecnicoId, TipoServico? Tipo);

public sealed record ArquivoExportado(byte[] Conteudo, string NomeDoArquivo, int Linhas);

/// <summary>
/// Planilha .xlsx com UMA linha por relatório emitido (número, data, tipo, executor, cliente, equipamento, total,
/// desconto, total geral, situação). **Sem CPF/CNPJ** (decisão do Owner). Cliente e equipamento saem dos snapshots da
/// emissão (o documento fiscal), não do cadastro vivo.
/// </summary>
public interface IExportadorDeRelatorios
{
    Task<Resultado<ArquivoExportado>> ExportarAsync(FiltroDeExportacao filtro, CancellationToken ct = default);
}
