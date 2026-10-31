using Jvd.Application.Comum;

namespace Jvd.Application.Emissor;

/// <summary>Dados editáveis do emissor (Specs/EMISSOR_CATALOGO.md). O serviço normaliza e valida.</summary>
public sealed record DadosDoEmissor(
    string RazaoSocial,
    string? NomeFantasia,
    string Cnpj,
    string? InscricaoEstadual,
    string? Logradouro,
    string? Numero,
    string? Bairro,
    string? Cep,
    string? Cidade,
    string? Uf,
    string? Telefones,
    string? NumeroAutorizacaoIpem,
    string? ResponsavelTecnicoNome,
    string? ResponsavelTecnicoCpf);

public sealed record EmissorResumo(
    string RazaoSocial,
    string? NomeFantasia,
    string Cnpj,
    string? InscricaoEstadual,
    string? Logradouro,
    string? Numero,
    string? Bairro,
    string? Cep,
    string? Cidade,
    string? Uf,
    string? Telefones,
    string? NumeroAutorizacaoIpem,
    string? ResponsavelTecnicoNome,
    string? ResponsavelTecnicoCpf,
    DateTime AtualizadoEm,
    string Versao);

/// <summary>Empresa emissora: existe no máximo uma. Técnico e gestor consultam; só o gestor grava.</summary>
public interface IEmissorService
{
    /// <summary><c>nao_encontrado</c> enquanto nunca foi configurado.</summary>
    Task<Resultado<EmissorResumo>> ObterAsync(CancellationToken ct = default);

    /// <summary>
    /// Cria na primeira vez (<paramref name="versao"/> deve ser nula) e altera depois (<paramref name="versao"/> obrigatória;
    /// versão velha = <c>conflito</c>).
    /// </summary>
    Task<Resultado<EmissorResumo>> SalvarAsync(DadosDoEmissor dados, string? versao, Guid atorId, CancellationToken ct = default);
}
