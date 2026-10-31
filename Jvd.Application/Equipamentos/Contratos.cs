using Jvd.Application.Comum;

namespace Jvd.Application.Equipamentos;

public sealed record DadosDoEquipamento(
    Guid ClienteId,
    string Descricao,
    string? NumeroSerie,
    string? NumeroInmetro,
    string? Observacoes);

public sealed record EquipamentoResumo(
    Guid Id,
    Guid ClienteId,
    string ClienteNome,
    string Descricao,
    string? NumeroSerie,
    string? NumeroInmetro,
    string? Observacoes,
    bool Ativo,
    DateTime CriadoEm,
    DateTime AtualizadoEm,
    string Versao);

public sealed record EquipamentoSalvo(EquipamentoResumo Equipamento, IReadOnlyList<Aviso> Avisos);

public sealed record FiltroDeEquipamentos(
    Guid? ClienteId, string? Busca, bool? Ativo, int Pagina = 1, int Tamanho = 20);

/// <summary>
/// Cadastro de equipamentos (balanças). Mesmas permissões do cliente: técnico e gestor criam/editam, só o gestor
/// desativa/reativa, o técnico só enxerga equipamentos ativos (de clientes ativos).
/// </summary>
public interface IEquipamentoService
{
    Task<PaginaDeResultados<EquipamentoResumo>> ListarAsync(FiltroDeEquipamentos filtro, bool incluirInativos, CancellationToken ct = default);

    Task<Resultado<EquipamentoResumo>> ObterAsync(Guid id, bool incluirInativos, CancellationToken ct = default);

    Task<Resultado<EquipamentoSalvo>> CriarAsync(DadosDoEquipamento dados, Guid atorId, CancellationToken ct = default);

    /// <summary>Alterar o <c>ClienteId</c> transfere a balança para outro cliente (auditado).</summary>
    Task<Resultado<EquipamentoSalvo>> AtualizarAsync(
        Guid id, DadosDoEquipamento dados, string versao, Guid atorId, bool podeEditarInativo, CancellationToken ct = default);

    Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default);

    Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default);
}
