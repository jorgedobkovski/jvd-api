using Jvd.Application.Relatorios;

namespace jvd_api.Contratos;

public sealed record TotaisDoPeriodoResponse(int Quantidade, decimal Total, decimal Descontos, decimal TotalGeral, decimal TicketMedio);

public sealed record IndicadorPorExecutorResponse(Guid TecnicoId, string TecnicoNome, int Quantidade, decimal TotalGeral);

public sealed record IndicadorPorTipoResponse(string Tipo, int Quantidade, decimal TotalGeral);

public sealed record IndicadorPorClienteResponse(Guid ClienteId, string ClienteNome, int Quantidade, decimal TotalGeral);

public sealed record IndicadoresResponse(
    DateOnly? De,
    DateOnly? Ate,
    TotaisDoPeriodoResponse Totais,
    IReadOnlyList<IndicadorPorExecutorResponse> PorExecutor,
    IReadOnlyList<IndicadorPorTipoResponse> PorTipo,
    IReadOnlyList<IndicadorPorClienteResponse> MaioresClientes)
{
    public static IndicadoresResponse Criar(Indicadores i) => new(
        i.De, i.Ate,
        new TotaisDoPeriodoResponse(i.Totais.Quantidade, i.Totais.Total, i.Totais.Descontos, i.Totais.TotalGeral, i.Totais.TicketMedio),
        i.PorExecutor.Select(x => new IndicadorPorExecutorResponse(x.TecnicoId, x.TecnicoNome, x.Quantidade, x.TotalGeral)).ToList(),
        i.PorTipo.Select(x => new IndicadorPorTipoResponse(x.Tipo.ToString(), x.Quantidade, x.TotalGeral)).ToList(),
        i.MaioresClientes.Select(x => new IndicadorPorClienteResponse(x.ClienteId, x.ClienteNome, x.Quantidade, x.TotalGeral)).ToList());
}
