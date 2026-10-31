using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Clientes;
using Jvd.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>
/// Cadastro de clientes (Specs/CLIENTES_EQUIPAMENTOS.md). Técnico e gestor listam, criam e editam; só o gestor
/// desativa/reativa e enxerga inativos.
/// </summary>
[ApiController]
[Route("api/clientes")]
[Authorize(Policy = Politicas.Tecnico)]
public sealed class ClientesController(IClienteService servico) : ControllerBase
{
    private bool EhGestor => User.IsInRole(nameof(PerfilUsuario.Gestor));

    [HttpGet]
    [ProducesResponseType<PaginaResponse<ClienteResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(
        [FromQuery] string? busca, [FromQuery] string? uf, [FromQuery] bool? ativo, [FromQuery] bool? incompleto,
        [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default)
    {
        var resultado = await servico.ListarAsync(new FiltroDeClientes(busca, uf, ativo, incompleto, pagina, tamanho), EhGestor, ct);
        return Ok(PaginaResponse<ClienteResponse>.De(resultado, ClienteResponse.De));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ClienteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct)
    {
        var resultado = await servico.ObterAsync(id, EhGestor, ct);
        return resultado.Sucesso ? Ok(ClienteResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    [HttpPost]
    [ProducesResponseType<ClienteSalvoResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Criar([FromBody] ClienteRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.CriarAsync(pedido.ParaDados(), atorId, ct);
        return resultado.Sucesso
            ? CreatedAtAction(nameof(Obter), new { id = resultado.Valor!.Cliente.Id }, ClienteSalvoResponse.De(resultado.Valor))
            : this.Problema(resultado);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<ClienteSalvoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarClienteRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.AtualizarAsync(id, pedido.ParaDados(), pedido.Versao, atorId, podeEditarInativo: EhGestor, ct);
        return resultado.Sucesso ? Ok(ClienteSalvoResponse.De(resultado.Valor!)) : this.Problema(resultado);
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
