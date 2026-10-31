using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Equipamentos;
using Jvd.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>
/// Cadastro de equipamentos (balanças) — mesmas permissões dos clientes. Alterar o <c>clienteId</c> transfere o
/// equipamento para outro cliente.
/// </summary>
[ApiController]
[Route("api/equipamentos")]
[Authorize(Policy = Politicas.Tecnico)]
public sealed class EquipamentosController(IEquipamentoService servico) : ControllerBase
{
    private bool EhGestor => User.IsInRole(nameof(PerfilUsuario.Gestor));

    [HttpGet]
    [ProducesResponseType<PaginaResponse<EquipamentoResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(
        [FromQuery] Guid? clienteId, [FromQuery] string? busca, [FromQuery] bool? ativo,
        [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default)
    {
        var resultado = await servico.ListarAsync(new FiltroDeEquipamentos(clienteId, busca, ativo, pagina, tamanho), EhGestor, ct);
        return Ok(PaginaResponse<EquipamentoResponse>.De(resultado, EquipamentoResponse.De));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EquipamentoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct)
    {
        var resultado = await servico.ObterAsync(id, EhGestor, ct);
        return resultado.Sucesso ? Ok(EquipamentoResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    [HttpPost]
    [ProducesResponseType<EquipamentoSalvoResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Criar([FromBody] EquipamentoRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.CriarAsync(pedido.ParaDados(), atorId, ct);
        return resultado.Sucesso
            ? CreatedAtAction(nameof(Obter), new { id = resultado.Valor!.Equipamento.Id }, EquipamentoSalvoResponse.De(resultado.Valor))
            : this.Problema(resultado);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<EquipamentoSalvoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarEquipamentoRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.AtualizarAsync(id, pedido.ParaDados(), pedido.Versao, atorId, podeEditarInativo: EhGestor, ct);
        return resultado.Sucesso ? Ok(EquipamentoSalvoResponse.De(resultado.Valor!)) : this.Problema(resultado);
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
