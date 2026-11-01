using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>
/// Visão gerencial (RF-033/034) — SOMENTE o gestor: valores, indicadores e exportação são exclusivos dele
/// (Specs/AUTENTICACAO_PERMISSOES.md). Só relatórios emitidos. As respostas nunca vão para cache.
/// </summary>
[ApiController]
[Route("api/gerencial")]
[Authorize(Policy = Politicas.Gestor)]
public sealed class GerencialController(IIndicadoresService indicadores, IExportadorDeRelatorios exportador, ILogger<GerencialController> log) : ControllerBase
{
    private const string TipoDaPlanilha = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Totais, ranking por executor, por tipo de serviço e maiores clientes, no período da data do serviço.</summary>
    [HttpGet("indicadores")]
    [ProducesResponseType<IndicadoresResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Indicadores([FromQuery] DateOnly? de, [FromQuery] DateOnly? ate, CancellationToken ct)
    {
        var resultado = await indicadores.ObterAsync(de, ate, ct);
        if (!resultado.Sucesso) return this.Problema(resultado);

        Response.Headers.CacheControl = "no-store";
        return Ok(IndicadoresResponse.Criar(resultado.Valor!));
    }

    /// <summary>Planilha .xlsx dos relatórios emitidos, com os mesmos filtros da listagem. Sem CPF/CNPJ.</summary>
    [HttpGet("exportar-relatorios")]
    [Produces(TipoDaPlanilha)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportarRelatorios(
        [FromQuery] DateOnly? de, [FromQuery] DateOnly? ate, [FromQuery] Guid? clienteId, [FromQuery] Guid? equipamentoId,
        [FromQuery] Guid? tecnicoId, [FromQuery] string? tipo, CancellationToken ct)
    {
        TipoServico? filtroTipo = null;
        if (!string.IsNullOrWhiteSpace(tipo))
        {
            if (!Enumeracoes.TentarConverter<TipoServico>(tipo, out var t))
            {
                return this.Problema(Resultado.Falha(new ErroNegocio(CodigosDeErro.Validacao, "Tipo de serviço inválido.")));
            }

            filtroTipo = t;
        }

        var resultado = await exportador.ExportarAsync(new FiltroDeExportacao(de, ate, clienteId, equipamentoId, tecnicoId, filtroTipo), ct);
        if (!resultado.Sucesso) return this.Problema(resultado);

        var arquivo = resultado.Valor!;
        // Só a quantidade e quem pediu: nada do conteúdo (nomes, valores) vai para o log.
        UsuarioAtual.TentarObterId(User, out var usuario);
        log.LogInformation("Exportação de relatórios gerada: {Linhas} linha(s) pelo usuário {UsuarioId}.", arquivo.Linhas, usuario);

        Response.Headers.CacheControl = "no-store";
        return File(arquivo.Conteudo, TipoDaPlanilha, arquivo.NomeDoArquivo);
    }
}
