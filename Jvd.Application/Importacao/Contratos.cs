using Jvd.Application.Comum;
using Jvd.Domain;

namespace Jvd.Application.Importacao;

/// <summary>Uma linha da planilha: número (começando em 1) e o texto de cada célula preenchida, por letra de coluna (A, B, …).</summary>
public sealed record LinhaDaPlanilha(int Numero, IReadOnlyDictionary<string, string> Celulas)
{
    public string? Valor(string coluna) =>
        Celulas.TryGetValue(coluna, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>Lê a primeira planilha de um arquivo .xlsx. Implementada na Infrastructure; falha com <see cref="PlanilhaInvalidaException"/>.</summary>
public interface ILeitorDePlanilha
{
    IReadOnlyList<LinhaDaPlanilha> Ler(Stream arquivo);
}

/// <summary>O arquivo não é um .xlsx legível ou excede os limites de segurança. A mensagem nunca traz conteúdo do arquivo.</summary>
public sealed class PlanilhaInvalidaException(string mensagem) : Exception(mensagem);

/// <summary>Ocorrência de uma linha: número e motivo FIXO (nunca o conteúdo da linha).</summary>
public sealed record Ocorrencia(int Linha, GravidadeOcorrencia Gravidade, string Motivo);

public sealed record RelatorioDeImportacao(
    Guid? ImportacaoId,
    TipoImportacao Tipo,
    bool Simulacao,
    int TotalLidos,
    int TotalImportados,
    int TotalRejeitados,
    int TotalAvisos,
    int TotalIgnoradas,
    IReadOnlyList<Ocorrencia> Ocorrencias);

public sealed record ImportacaoResumo(
    Guid Id,
    TipoImportacao Tipo,
    string NomeArquivo,
    Guid UsuarioId,
    DateTime ExecutadaEm,
    int TotalLidos,
    int TotalImportados,
    int TotalRejeitados,
    int TotalAvisos,
    int TotalIgnoradas);

public sealed record ImportacaoDetalhe(ImportacaoResumo Resumo, IReadOnlyList<Ocorrencia> Ocorrencias);

/// <summary>
/// Carga das planilhas do ERP (Specs/IMPORTACAO.md). Só o gestor. Com <c>simular</c> nada é gravado; a execução real
/// grava os registros e o registro da importação numa só transação.
/// </summary>
public interface IImportacaoService
{
    Task<Resultado<RelatorioDeImportacao>> ImportarClientesAsync(
        Stream arquivo, string nomeArquivo, bool simular, Guid atorId, CancellationToken ct = default);

    Task<Resultado<RelatorioDeImportacao>> ImportarServicosAsync(
        Stream arquivo, string nomeArquivo, bool simular, Guid atorId, CancellationToken ct = default);

    Task<PaginaDeResultados<ImportacaoResumo>> ListarAsync(int pagina, int tamanho, CancellationToken ct = default);

    Task<Resultado<ImportacaoDetalhe>> ObterAsync(Guid id, CancellationToken ct = default);
}
