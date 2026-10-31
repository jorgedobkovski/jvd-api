using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jvd_api.Controllers;

[ApiController]
[Route("api/saude")]
public sealed class SaudeController : ControllerBase
{
    /// <summary>Verificação de que a API está no ar. Não toca o banco nem expõe versão ou configuração.</summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Verificar() => Ok(new { status = "ok" });
}
