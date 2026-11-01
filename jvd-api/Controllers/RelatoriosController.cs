using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>
/// Relatório de Serviço (Specs/RELATORIO_SERVICO.md). Técnico e gestor usam; o técnico só enxerga e age nos relatórios
/// em que é o executor (o serviço aplica a regra), e o gestor, em todos.
/// </summary>
[ApiController]
[Route("api/relatorios")]
[Authorize(Policy = Politicas.Tecnico)]
public sealed class RelatoriosController(IRelatorioService servico) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PaginaResponse<RelatorioResumoResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar(
        [FromQuery] Guid? clienteId, [FromQuery] Guid? equipamentoId, [FromQuery] Guid? tecnicoId,
        [FromQuery] string? status, [FromQuery] string? tipo, [FromQuery] DateOnly? de, [FromQuery] DateOnly? ate,
        [FromQuery] int? numero, [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default)
    {
        StatusRelatorio? filtroStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enumeracoes.TentarConverter<StatusRelatorio>(status, out var s)) return Invalido("Status inválido. Use Rascunho, Emitido ou Cancelado.");
            filtroStatus = s;
        }

        TipoServico? filtroTipo = null;
        if (!string.IsNullOrWhiteSpace(tipo))
        {
            if (!Enumeracoes.TentarConverter<TipoServico>(tipo, out var t)) return Invalido("Tipo de serviço inválido.");
            filtroTipo = t;
        }

        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var filtro = new FiltroDeRelatorios(clienteId, equipamentoId, tecnicoId, filtroStatus, filtroTipo, de, ate, numero, pagina, tamanho);
        var resultado = await servico.ListarAsync(filtro, ator, ct);
        return Ok(PaginaResponse<RelatorioResumoResponse>.De(resultado, RelatorioResumoResponse.De));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<RelatorioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct)
    {
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.ObterAsync(id, ator, ct);
        return resultado.Sucesso ? Ok(RelatorioResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    [HttpPost]
    [ProducesResponseType<RelatorioResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Criar([FromBody] RelatorioRequest pedido, CancellationToken ct)
    {
        if (!Enumeracoes.TentarConverter<TipoServico>(pedido.TipoServico, out var tipo)) return Invalido("Tipo de serviço inválido.");
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.CriarRascunhoAsync(pedido.ParaDados(tipo), ator, ct);
        return resultado.Sucesso
            ? CreatedAtAction(nameof(Obter), new { id = resultado.Valor!.Id }, RelatorioResponse.De(resultado.Valor))
            : this.Problema(resultado);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<RelatorioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarRelatorioRequest pedido, CancellationToken ct)
    {
        if (!Enumeracoes.TentarConverter<TipoServico>(pedido.TipoServico, out var tipo)) return Invalido("Tipo de serviço inválido.");
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.AtualizarRascunhoAsync(id, pedido.ParaDados(tipo), pedido.Versao, ator, ct);
        return resultado.Sucesso ? Ok(RelatorioResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    /// <summary>Apaga um rascunho (nunca teve número). Um relatório emitido não se apaga.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Descartar(Guid id, CancellationToken ct)
    {
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.DescartarRascunhoAsync(id, ator, ct);
        return resultado.Sucesso ? NoContent() : this.Problema(resultado);
    }

    [HttpPost("{id:guid}/emitir")]
    [ProducesResponseType<RelatorioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Emitir(Guid id, [FromBody] VersaoRequest pedido, CancellationToken ct)
    {
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.EmitirAsync(id, pedido.Versao, ator, ct);
        return resultado.Sucesso ? Ok(RelatorioResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    /// <summary>Altera um relatório emitido com histórico: exige motivo, mantém o número e incrementa a revisão.</summary>
    [HttpPost("{id:guid}/retificar")]
    [ProducesResponseType<RelatorioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retificar(Guid id, [FromBody] RetificarRelatorioRequest pedido, CancellationToken ct)
    {
        if (!Enumeracoes.TentarConverter<TipoServico>(pedido.TipoServico, out var tipo)) return Invalido("Tipo de serviço inválido.");
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.RetificarAsync(id, pedido.ParaDados(tipo), pedido.Versao, ator, ct);
        return resultado.Sucesso ? Ok(RelatorioResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    /// <summary>Cancela um relatório emitido (motivo obrigatório). O número é mantido e nunca reutilizado.</summary>
    [HttpPost("{id:guid}/cancelar")]
    [ProducesResponseType<RelatorioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancelar(Guid id, [FromBody] CancelarRelatorioRequest pedido, CancellationToken ct)
    {
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.CancelarAsync(id, pedido.Motivo, pedido.Versao, ator, ct);
        return resultado.Sucesso ? Ok(RelatorioResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    /// <summary>Histórico de retificações (mesmo acesso do relatório: o técnico só vê o dos seus).</summary>
    [HttpGet("{id:guid}/revisoes")]
    [ProducesResponseType<IReadOnlyList<RevisaoResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revisoes(Guid id, CancellationToken ct)
    {
        if (!TentarObterAtor(out var ator)) return Unauthorized();

        var resultado = await servico.ListarRevisoesAsync(id, ator, ct);
        return resultado.Sucesso ? Ok(resultado.Valor!.Select(RevisaoResponse.De).ToList()) : this.Problema(resultado);
    }

    /// <summary>O ator vem SEMPRE do token (id e perfil), nunca do corpo ou da rota.</summary>
    private bool TentarObterAtor(out AtorDoRelatorio ator)
    {
        ator = default!;
        if (!UsuarioAtual.TentarObterId(User, out var id)) return false;

        ator = new AtorDoRelatorio(id, User.IsInRole(nameof(PerfilUsuario.Gestor)));
        return true;
    }

    private IActionResult Invalido(string mensagem) =>
        this.Problema(Resultado.Falha(new ErroNegocio(CodigosDeErro.Validacao, mensagem)));
}
