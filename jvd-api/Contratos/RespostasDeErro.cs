using Jvd.Application.Comum;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Contratos;

/// <summary>Converte erros de negócio em ProblemDetails (RFC 9457), com o código estável em <c>codigo</c>.</summary>
public static class RespostasDeErro
{
    public static IActionResult Problema(this ControllerBase controller, Resultado resultado)
    {
        var primeiro = resultado.Erros[0];
        var status = primeiro.Codigo switch
        {
            CodigosDeErro.NaoEncontrado => StatusCodes.Status404NotFound,
            CodigosDeErro.EmailEmUso or CodigosDeErro.ArquivoJaImportado or CodigosDeErro.RelatorioNaoERascunho or CodigosDeErro.RelatorioNaoEmitido or CodigosDeErro.UltimoGestor or CodigosDeErro.NaoPodeAgirEmSi or CodigosDeErro.Conflito
                => StatusCodes.Status409Conflict,
            CodigosDeErro.SessaoInvalida => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest
        };

        var problema = new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                StatusCodes.Status404NotFound => "Não encontrado.",
                StatusCodes.Status409Conflict => "Conflito.",
                StatusCodes.Status401Unauthorized => "Não autorizado.",
                _ => "Requisição inválida."
            },
            Detail = primeiro.Mensagem
        };
        problema.Extensions["codigo"] = primeiro.Codigo;
        problema.Extensions["erros"] = resultado.Erros.Select(e => new { codigo = e.Codigo, mensagem = e.Mensagem }).ToList();

        return new ObjectResult(problema) { StatusCode = status };
    }
}
