using System.Threading.RateLimiting;
using jvd_api.Controllers;
using jvd_api.Seguranca;
using Jvd.Infrastructure;
using Jvd.Infrastructure.Autenticacao;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.AddJvdInfrastructure(builder.Configuration);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigurarJwtBearer>();
builder.Services.AddAuthorization(Politicas.Configurar);

// CORS: só as origens do app, em Cors:AllowedOrigins. Sem credenciais (o token vai no cabeçalho).
// Lido da configuração final (lazy), para ser sobrescrevível nos testes.
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IConfiguration>((cors, configuracao) =>
{
    var origens = configuracao.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    cors.AddPolicy("app", p => p
        .WithOrigins(origens)
        .WithHeaders("Authorization", "Content-Type")
        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE"));
});

// Limite de tentativas nos endpoints de autenticação (por IP), contra força bruta.
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((limite, configuracao) =>
{
    var porMinuto = configuracao.GetValue("RateLimit:AutenticacaoPorMinuto", 10);
    limite.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limite.AddPolicy(AutenticacaoController.PoliticaDeLimite, contexto =>
        RateLimitPartition.GetFixedWindowLimiter(
            ParticaoPorIp.Chave(contexto.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = porMinuto,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

// Erros inesperados viram ProblemDetails genérico (sem stack trace, sem dados pessoais).
app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.Use(async (contexto, proximo) =>
{
    contexto.Response.Headers.XContentTypeOptions = "nosniff";
    contexto.Response.Headers.XFrameOptions = "DENY";
    contexto.Response.Headers["Referrer-Policy"] = "no-referrer";
    await proximo();
});

app.UseCors("app");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Primeiro gestor: só roda se Seed:Habilitado=true e não houver usuário algum. O banco precisa estar migrado.
if (app.Configuration.GetValue<bool>($"{SeedOptions.Secao}:Habilitado"))
{
    using var escopo = app.Services.CreateScope();
    await escopo.ServiceProvider.GetRequiredService<UsuarioSeeder>().ExecutarAsync();
}

app.Run();

// Necessário para os testes de integração (WebApplicationFactory<Program>).
public partial class Program;
