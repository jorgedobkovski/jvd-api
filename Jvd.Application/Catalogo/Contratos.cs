using Jvd.Application.Comum;

namespace Jvd.Application.Catalogo;

public sealed record DadosDoServico(string Descricao, string? CodigoOrigem, decimal? PrecoSugerido);

public sealed record ServicoResumo(
    Guid Id,
    string? CodigoOrigem,
    string Descricao,
    decimal? PrecoSugerido,
    bool Ativo,
    DateTime CriadoEm,
    DateTime AtualizadoEm,
    string Versao);

public sealed record ServicoSalvo(ServicoResumo Servico, IReadOnlyList<Aviso> Avisos);

public sealed record FiltroDeServicos(string? Busca, bool? Ativo, int Pagina = 1, int Tamanho = 20);

/// <summary>
/// Catálogo de serviços. O gestor faz o CRUD completo; o técnico só lê itens ativos — quem chama passa
/// <c>incluirInativos</c> conforme o perfil, e o serviço aplica a regra.
/// </summary>
public interface ICatalogoServicoService
{
    Task<PaginaDeResultados<ServicoResumo>> ListarAsync(FiltroDeServicos filtro, bool incluirInativos, CancellationToken ct = default);

    Task<Resultado<ServicoResumo>> ObterAsync(Guid id, bool incluirInativos, CancellationToken ct = default);

    Task<Resultado<ServicoSalvo>> CriarAsync(DadosDoServico dados, Guid atorId, CancellationToken ct = default);

    Task<Resultado<ServicoSalvo>> AtualizarAsync(Guid id, DadosDoServico dados, string versao, Guid atorId, CancellationToken ct = default);

    Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default);

    Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default);
}
