using Jvd.Application.Comum;
using Jvd.Domain;

namespace Jvd.Application.Relatorios;

/// <summary>Quem está agindo (sempre vindo do token). O técnico só enxerga os relatórios em que é o executor.</summary>
public sealed record AtorDoRelatorio(Guid Id, bool EhGestor);

/// <summary>
/// Dados editáveis do rascunho (Specs/RELATORIO_SERVICO.md). <c>TecnicoId</c> nulo = o próprio ator ao criar, e
/// "manter o executor atual" ao editar.
/// </summary>
public sealed record DadosDoRelatorio(
    Guid ClienteId,
    Guid EquipamentoId,
    Guid? TecnicoId,
    TipoServico TipoServico,
    DateOnly DataServico,
    string? ServicoExecutado,
    string? Observacoes,
    DateOnly? VerificacaoAnteriorData,
    string? VerificacaoAnteriorCertificado,
    string? NumeroSeloReparador,
    int GarantiaDias,
    decimal Total,
    decimal Desconto,
    Guid? CatalogoServicoId);

// ---------- snapshots (cópia no momento da emissão) ----------

public sealed record SnapshotDoCliente(
    string Nome, string? NomeFantasia, string? Documento, string? InscricaoEstadualRg, string? Logradouro, string? Numero,
    string? Bairro, string? Cep, string? Cidade, string? Uf, string? Telefone);

public sealed record SnapshotDoEquipamento(string Descricao, string? NumeroSerie, string? NumeroInmetro);

public sealed record SnapshotDoEmissor(
    string RazaoSocial, string? NomeFantasia, string Cnpj, string? InscricaoEstadual, string? Logradouro, string? Numero,
    string? Bairro, string? Cep, string? Cidade, string? Uf, string? Telefones, string? NumeroAutorizacaoIpem,
    string? ResponsavelTecnicoNome, string? ResponsavelTecnicoCpf);

// ---------- leitura ----------

public sealed record RelatorioResumo(
    Guid Id,
    int? Numero,
    string? NumeroFormatado,
    StatusRelatorio Status,
    TipoServico TipoServico,
    DateOnly DataServico,
    Guid ClienteId,
    string ClienteNome,
    Guid EquipamentoId,
    string EquipamentoDescricao,
    Guid TecnicoId,
    string TecnicoNome,
    decimal TotalGeral,
    int Revisao);

public sealed record RelatorioDetalhe(
    Guid Id,
    int? Numero,
    string? NumeroFormatado,
    StatusRelatorio Status,
    TipoServico TipoServico,
    DateOnly DataServico,
    Guid ClienteId,
    string ClienteNome,
    Guid EquipamentoId,
    string EquipamentoDescricao,
    Guid TecnicoId,
    string TecnicoNome,
    string ServicoExecutado,
    string? Observacoes,
    DateOnly? VerificacaoAnteriorData,
    string? VerificacaoAnteriorCertificado,
    string? NumeroSeloReparador,
    int GarantiaDias,
    string GarantiaExtenso,
    decimal Total,
    decimal Desconto,
    decimal TotalGeral,
    Guid? CatalogoServicoId,
    int Revisao,
    SnapshotDoCliente? Cliente,
    SnapshotDoEquipamento? Equipamento,
    SnapshotDoEmissor? Emissor,
    DateTime? EmitidoEm,
    Guid? EmitidoPor,
    DateTime? CanceladoEm,
    Guid? CanceladoPor,
    string? MotivoCancelamento,
    DateTime CriadoEm,
    DateTime AtualizadoEm,
    string Versao);

/// <summary>
/// Dados de uma retificação. Cliente, equipamento e executor NÃO mudam por retificação (cancela-se e emite-se outro).
/// <c>AtualizarDadosDeCadastro</c> refaz os snapshots do cliente e do equipamento a partir do cadastro atual.
/// </summary>
public sealed record DadosDaRetificacao(
    string? Motivo,
    TipoServico TipoServico,
    DateOnly DataServico,
    string? ServicoExecutado,
    string? Observacoes,
    DateOnly? VerificacaoAnteriorData,
    string? VerificacaoAnteriorCertificado,
    string? NumeroSeloReparador,
    int GarantiaDias,
    decimal Total,
    decimal Desconto,
    Guid? CatalogoServicoId,
    bool AtualizarDadosDeCadastro);

/// <summary>Uma linha do histórico. Os JSON têm só os campos alterados (valores antes e depois) e podem conter dados pessoais.</summary>
public sealed record RevisaoDoRelatorio(
    int Revisao,
    DateTime RetificadoEm,
    Guid RetificadoPor,
    string RetificadoPorNome,
    string Motivo,
    System.Text.Json.JsonElement DadosAnteriores,
    System.Text.Json.JsonElement DadosNovos);

public sealed record FiltroDeRelatorios(
    Guid? ClienteId, Guid? EquipamentoId, Guid? TecnicoId, StatusRelatorio? Status, TipoServico? Tipo,
    DateOnly? De, DateOnly? Ate, int? Numero, int Pagina = 1, int Tamanho = 20);

/// <summary>
/// Núcleo do sistema. Rascunho → emissão (número sem buracos + snapshots) → (4B) retificação e cancelamento. O técnico
/// só enxerga e age nos relatórios em que é o executor; relatório alheio é "não encontrado". Toda alteração exige a
/// <c>versao</c> que a pessoa viu.
/// </summary>
public interface IRelatorioService
{
    Task<PaginaDeResultados<RelatorioResumo>> ListarAsync(FiltroDeRelatorios filtro, AtorDoRelatorio ator, CancellationToken ct = default);

    Task<Resultado<RelatorioDetalhe>> ObterAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default);

    Task<Resultado<RelatorioDetalhe>> CriarRascunhoAsync(DadosDoRelatorio dados, AtorDoRelatorio ator, CancellationToken ct = default);

    Task<Resultado<RelatorioDetalhe>> AtualizarRascunhoAsync(
        Guid id, DadosDoRelatorio dados, string versao, AtorDoRelatorio ator, CancellationToken ct = default);

    /// <summary>Apaga um rascunho (nunca teve número nem circulou).</summary>
    Task<Resultado> DescartarRascunhoAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default);

    /// <summary>Atribui o número, grava os snapshots e fecha o relatório para edição direta.</summary>
    Task<Resultado<RelatorioDetalhe>> EmitirAsync(Guid id, string versao, AtorDoRelatorio ator, CancellationToken ct = default);

    /// <summary>
    /// Altera um relatório emitido com histórico: exige motivo, mantém o número, incrementa a revisão e guarda o que
    /// mudou, quem e quando.
    /// </summary>
    Task<Resultado<RelatorioDetalhe>> RetificarAsync(
        Guid id, DadosDaRetificacao dados, string versao, AtorDoRelatorio ator, CancellationToken ct = default);

    /// <summary>Cancela um relatório emitido (motivo obrigatório). O número é mantido e nunca reutilizado.</summary>
    Task<Resultado<RelatorioDetalhe>> CancelarAsync(
        Guid id, string? motivo, string versao, AtorDoRelatorio ator, CancellationToken ct = default);

    /// <summary>Histórico de retificações, da mais antiga para a mais nova. Mesmo acesso do relatório.</summary>
    Task<Resultado<IReadOnlyList<RevisaoDoRelatorio>>> ListarRevisoesAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default);
}
