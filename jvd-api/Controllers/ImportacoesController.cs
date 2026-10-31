using jvd_api.Contratos;
using jvd_api.Seguranca;
using Jvd.Application.Importacao;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

/// <summary>
/// Carga das planilhas do ERP (Specs/IMPORTACAO.md) — somente o gestor. O envio é <c>multipart/form-data</c> com o
/// campo <c>arquivo</c> (.xlsx, até 10 MB). <c>simular</c> vale <c>true</c> por padrão: só <c>simular=false</c> grava.
/// </summary>
[ApiController]
[Route("api/importacoes")]
[Authorize(Policy = Politicas.Gestor)]
public sealed class ImportacoesController(IImportacaoService servico) : ControllerBase
{
    private const long LimiteDoEnvio = 10 * 1024 * 1024;

    [HttpPost("clientes")]
    [RequestSizeLimit(LimiteDoEnvio + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = LimiteDoEnvio + 64 * 1024)]
    [ProducesResponseType<RelatorioDeImportacaoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ImportarClientes(IFormFile? arquivo, [FromQuery] bool simular = true, CancellationToken ct = default) =>
        Importar(arquivo, simular, servico.ImportarClientesAsync, ct);

    [HttpPost("servicos")]
    [RequestSizeLimit(LimiteDoEnvio + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = LimiteDoEnvio + 64 * 1024)]
    [ProducesResponseType<RelatorioDeImportacaoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ImportarServicos(IFormFile? arquivo, [FromQuery] bool simular = true, CancellationToken ct = default) =>
        Importar(arquivo, simular, servico.ImportarServicosAsync, ct);

    [HttpGet]
    [ProducesResponseType<PaginaResponse<ImportacaoResumoResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar([FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default)
    {
        var resultado = await servico.ListarAsync(pagina, tamanho, ct);
        return Ok(PaginaResponse<ImportacaoResumoResponse>.De(resultado, ImportacaoResumoResponse.De));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ImportacaoDetalheResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct)
    {
        var resultado = await servico.ObterAsync(id, ct);
        return resultado.Sucesso ? Ok(ImportacaoDetalheResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }

    private async Task<IActionResult> Importar(
        IFormFile? arquivo, bool simular,
        Func<Stream, string, bool, Guid, CancellationToken, Task<Jvd.Application.Comum.Resultado<RelatorioDeImportacao>>> acao,
        CancellationToken ct)
    {
        if (!UsuarioAtual.TentarObterId(User, out var atorId)) return Unauthorized();

        if (arquivo is null || arquivo.Length == 0)
        {
            return this.Problema(Jvd.Application.Comum.Resultado.Falha(new Jvd.Application.Comum.ErroNegocio(
                Jvd.Application.Comum.CodigosDeErro.Validacao, "Envie o arquivo .xlsx no campo 'arquivo'.")));
        }

        if (!string.Equals(Path.GetExtension(arquivo.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return this.Problema(Jvd.Application.Comum.Resultado.Falha(new Jvd.Application.Comum.ErroNegocio(
                Jvd.Application.Comum.CodigosDeErro.Validacao, "O arquivo precisa ser uma planilha .xlsx.")));
        }

        await using var fluxo = arquivo.OpenReadStream();
        var resultado = await acao(fluxo, arquivo.FileName, simular, atorId, ct);
        return resultado.Sucesso ? Ok(RelatorioDeImportacaoResponse.De(resultado.Valor!)) : this.Problema(resultado);
    }
}
