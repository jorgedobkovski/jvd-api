using Jvd.Application.Comum;

namespace Jvd.Application.Relatorios;

/// <summary>Finalidade da via impressa (Specs/RELATORIO_SERVICO.md): muda só o rótulo, o conteúdo é o mesmo.</summary>
public enum ViaDoRelatorio
{
    Controle = 1,
    Cliente = 2,
    Fiscalizacao = 3
}

public sealed record ArquivoPdf(byte[] Conteudo, string NomeDoArquivo);

/// <summary>
/// Gera o PDF de um relatório emitido ou cancelado, na hora, a partir dos snapshots gravados (nada é guardado).
/// Usa as mesmas regras de acesso do <see cref="IRelatorioService"/>: o técnico só obtém o PDF dos seus.
/// </summary>
public interface IRelatorioPdfService
{
    Task<Resultado<ArquivoPdf>> GerarAsync(Guid id, ViaDoRelatorio via, AtorDoRelatorio ator, CancellationToken ct = default);
}
