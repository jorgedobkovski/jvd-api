using Jvd.Application.Importacao;

namespace jvd_api.Contratos;

public sealed record OcorrenciaResponse(int Linha, string Gravidade, string Motivo)
{
    public static OcorrenciaResponse De(Ocorrencia o) => new(o.Linha, o.Gravidade.ToString(), o.Motivo);
}

public sealed record RelatorioDeImportacaoResponse(
    Guid? ImportacaoId, string Tipo, bool Simulacao, int TotalLidos, int TotalImportados, int TotalRejeitados,
    int TotalAvisos, int TotalIgnoradas, IReadOnlyList<OcorrenciaResponse> Ocorrencias)
{
    public static RelatorioDeImportacaoResponse De(RelatorioDeImportacao r) => new(
        r.ImportacaoId, r.Tipo.ToString(), r.Simulacao, r.TotalLidos, r.TotalImportados, r.TotalRejeitados, r.TotalAvisos,
        r.TotalIgnoradas, r.Ocorrencias.Select(OcorrenciaResponse.De).ToList());
}

public sealed record ImportacaoResumoResponse(
    Guid Id, string Tipo, string NomeArquivo, Guid UsuarioId, DateTime ExecutadaEm, int TotalLidos, int TotalImportados,
    int TotalRejeitados, int TotalAvisos, int TotalIgnoradas)
{
    public static ImportacaoResumoResponse De(ImportacaoResumo i) => new(
        i.Id, i.Tipo.ToString(), i.NomeArquivo, i.UsuarioId, DateTime.SpecifyKind(i.ExecutadaEm, DateTimeKind.Utc),
        i.TotalLidos, i.TotalImportados, i.TotalRejeitados, i.TotalAvisos, i.TotalIgnoradas);
}

public sealed record ImportacaoDetalheResponse(ImportacaoResumoResponse Importacao, IReadOnlyList<OcorrenciaResponse> Ocorrencias)
{
    public static ImportacaoDetalheResponse De(ImportacaoDetalhe d) =>
        new(ImportacaoResumoResponse.De(d.Resumo), d.Ocorrencias.Select(OcorrenciaResponse.De).ToList());
}
