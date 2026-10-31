using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Emissor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>
/// Dados da empresa emissora e do responsável técnico (Specs/EMISSOR_CATALOGO.md). Técnico e gestor consultam;
/// só o gestor grava.
/// </summary>
[ApiController]
[Route("api/emissor")]
[Authorize(Policy = Politicas.Tecnico)]
public sealed class EmissorController(IEmissorService servico) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<EmissorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(CancellationToken ct)
    {
        var resultado = await servico.ObterAsync(ct);
        return resultado.Sucesso ? Ok(EmissorResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    /// <summary>Cria o emissor na primeira vez (sem <c>versao</c>) e o altera depois (com <c>versao</c>).</summary>
    [HttpPut]
    [Authorize(Policy = Politicas.Gestor)]
    [ProducesResponseType<EmissorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Salvar([FromBody] EmissorRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.SalvarAsync(pedido.ParaDados(), pedido.Versao, atorId, ct);
        return resultado.Sucesso ? Ok(EmissorResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }
}
