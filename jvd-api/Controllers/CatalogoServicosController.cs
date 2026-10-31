using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Catalogo;
using Jvd.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>
/// Catálogo de serviços (Specs/EMISSOR_CATALOGO.md). Técnico e gestor consultam (o técnico só vê itens ativos);
/// só o gestor cria, edita, desativa e reativa.
/// </summary>
[ApiController]
[Route("api/catalogo-servicos")]
[Authorize(Policy = Politicas.Tecnico)]
public sealed class CatalogoServicosController(ICatalogoServicoService servico) : ControllerBase
{
    private bool EhGestor => User.IsInRole(nameof(PerfilUsuario.Gestor));

    [HttpGet]
    [ProducesResponseType<PaginaResponse<ServicoResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(
        [FromQuery] string? busca, [FromQuery] bool? ativo,
        [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default)
    {
        var resultado = await servico.ListarAsync(new FiltroDeServicos(busca, ativo, pagina, tamanho), EhGestor, ct);
        return Ok(PaginaResponse<ServicoResponse>.De(resultado, ServicoResponse.De));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ServicoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct)
    {
        var resultado = await servico.ObterAsync(id, EhGestor, ct);
        return resultado.Sucesso ? Ok(ServicoResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Gestor)]
    [ProducesResponseType<ServicoSalvoResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Criar([FromBody] ServicoRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.CriarAsync(pedido.ParaDados(), atorId, ct);
        return resultado.Sucesso
            ? CreatedAtAction(nameof(Obter), new { id = resultado.Valor!.Servico.Id }, ServicoSalvoResponse.De(resultado.Valor))
            : this.Problema(resultado);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Politicas.Gestor)]
    [ProducesResponseType<ServicoSalvoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarServicoRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.AtualizarAsync(id, pedido.ParaDados(), pedido.Versao, atorId, ct);
        return resultado.Sucesso ? Ok(ServicoSalvoResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    [HttpPost("{id:guid}/desativar")]
    [Authorize(Policy = Politicas.Gestor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Desativar(Guid id, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.DesativarAsync(id, atorId, ct);
        return resultado.Sucesso ? NoContent() : this.Problema(resultado);
    }

    [HttpPost("{id:guid}/reativar")]
    [Authorize(Policy = Politicas.Gestor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reativar(Guid id, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.ReativarAsync(id, atorId, ct);
        return resultado.Sucesso ? NoContent() : this.Problema(resultado);
    }
}
