using Jvd.Application.Comum;
using Jvd.Domain;

namespace Jvd.Application.Clientes;

/// <summary>Dados editáveis de um cliente (Specs/CLIENTES_EQUIPAMENTOS.md). O serviço normaliza e valida.</summary>
public sealed record DadosDoCliente(
    string NomeRazaoSocial,
    string? NomeFantasia,
    string? Documento,
    string? InscricaoEstadualRg,
    string? Logradouro,
    string? Numero,
    string? Bairro,
    string? Cep,
    string? Cidade,
    string? Uf,
    string? Telefone,
    string? Email);

public sealed record ClienteResumo(
    Guid Id,
    string NomeRazaoSocial,
    string? NomeFantasia,
    string? Documento,
    string? InscricaoEstadualRg,
    string? Logradouro,
    string? Numero,
    string? Bairro,
    string? Cep,
    string? Cidade,
    string? Uf,
    string? Telefone,
    string? Email,
    bool CadastroIncompleto,
    OrigemCadastro Origem,
    bool Ativo,
    DateTime CriadoEm,
    DateTime AtualizadoEm,
    string Versao);

public sealed record ClienteSalvo(ClienteResumo Cliente, IReadOnlyList<Aviso> Avisos);

public sealed record FiltroDeClientes(
    string? Busca, string? Uf, bool? Ativo, bool? Incompleto, int Pagina = 1, int Tamanho = 20);

/// <summary>
/// Cadastro de clientes. Técnico e gestor criam e editam; só o gestor desativa/reativa. O técnico enxerga apenas
/// clientes ativos — quem chama passa <c>incluirInativos</c> conforme o perfil, e o serviço aplica a regra.
/// </summary>
public interface IClienteService
{
    Task<PaginaDeResultados<ClienteResumo>> ListarAsync(FiltroDeClientes filtro, bool incluirInativos, CancellationToken ct = default);

    Task<Resultado<ClienteResumo>> ObterAsync(Guid id, bool incluirInativos, CancellationToken ct = default);

    Task<Resultado<ClienteSalvo>> CriarAsync(DadosDoCliente dados, Guid atorId, CancellationToken ct = default);

    /// <param name="versao">Versão do registro que a pessoa estava vendo (devolvida em toda resposta).</param>
    /// <param name="podeEditarInativo">Só o gestor edita um cliente inativo.</param>
    Task<Resultado<ClienteSalvo>> AtualizarAsync(
        Guid id, DadosDoCliente dados, string versao, Guid atorId, bool podeEditarInativo, CancellationToken ct = default);

    Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default);

    Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default);
}
