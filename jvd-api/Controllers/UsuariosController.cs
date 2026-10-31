using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Comum;
using Jvd.Application.Usuarios;
using Jvd.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>Gestão de usuários — somente o gestor (Specs/AUTENTICACAO_PERMISSOES.md, ADR-006).</summary>
[ApiController]
[Route("api/usuarios")]
[Authorize(Policy = Politicas.Gestor)]
public sealed class UsuariosController(IUsuarioService servico) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PaginaResponse<UsuarioDetalheResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar(
        [FromQuery] string? busca, [FromQuery] string? perfil, [FromQuery] bool? ativo,
        [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default)
    {
        PerfilUsuario? filtroPerfil = null;
        if (!string.IsNullOrWhiteSpace(perfil))
        {
            if (!Perfis.TentarConverter(perfil, out var convertido)) return PerfilInvalido();
            filtroPerfil = convertido;
        }

        var resultado = await servico.ListarAsync(new FiltroDeUsuarios(busca, filtroPerfil, ativo, pagina, tamanho), ct);
        return Ok(PaginaResponse<UsuarioDetalheResponse>.De(resultado, UsuarioDetalheResponse.De));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<UsuarioDetalheResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct)
    {
        var resultado = await servico.ObterAsync(id, ct);
        return resultado.Sucesso ? Ok(UsuarioDetalheResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    [HttpPost]
    [ProducesResponseType<UsuarioDetalheResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Criar([FromBody] CriarUsuarioRequest pedido, CancellationToken ct)
    {
        if (!Perfis.TentarConverter(pedido.Perfil, out var perfil)) return PerfilInvalido();
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.CriarAsync(
            new CriarUsuarioComando(pedido.Nome, pedido.Email, perfil, pedido.Cpf, pedido.SenhaProvisoria, atorId), ct);

        return resultado.Sucesso
            ? CreatedAtAction(nameof(Obter), new { id = resultado.Valor!.Id }, UsuarioDetalheResponse.De(resultado.Valor))
            : this.Problema(resultado);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<UsuarioDetalheResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarUsuarioRequest pedido, CancellationToken ct)
    {
        if (!Perfis.TentarConverter(pedido.Perfil, out var perfil)) return PerfilInvalido();
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.AtualizarAsync(new AtualizarUsuarioComando(id, pedido.Nome, perfil, pedido.Cpf, atorId), ct);
        return resultado.Sucesso ? Ok(UsuarioDetalheResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    [HttpPost("{id:guid}/desativar")]
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reativar(Guid id, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.ReativarAsync(id, atorId, ct);
        return resultado.Sucesso ? NoContent() : this.Problema(resultado);
    }

    [HttpPost("{id:guid}/redefinir-senha")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RedefinirSenha(Guid id, [FromBody] RedefinirSenhaRequest pedido, CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        var resultado = await servico.RedefinirSenhaAsync(id, pedido.NovaSenhaProvisoria, atorId, ct);
        return resultado.Sucesso ? NoContent() : this.Problema(resultado);
    }

    private IActionResult PerfilInvalido() => this.Problema(Resultado.Falha(
        new ErroNegocio(CodigosDeErro.Validacao, "Perfil inválido. Use Tecnico ou Gestor.")));
}
