using Jvd.Domain;
using Jvd.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jvd.Infrastructure.Autenticacao;

/// <summary>
/// Configuração do primeiro gestor. Os valores vêm de user-secrets/variáveis de ambiente
/// (<c>Seed:*</c>); nada disto é versionado. Desligado por padrão.
/// </summary>
public sealed class SeedOptions
{
    public const string Secao = "Seed";

    public bool Habilitado { get; set; }
    public string GestorNome { get; set; } = "";
    public string GestorEmail { get; set; } = "";
    public string GestorSenha { get; set; } = "";
}

/// <summary>
/// Cria o primeiro gestor <b>somente se não existir nenhum usuário</b>. Nunca registra e-mail nem senha em log.
/// Exige que o banco já esteja migrado (a migration é aplicada explicitamente, nunca no startup).
/// </summary>
public sealed class UsuarioSeeder(
    UserManager<Usuario> usuarios,
    IOptions<SeedOptions> opcoes,
    TimeProvider relogio,
    ILogger<UsuarioSeeder> log)
{
    public async Task ExecutarAsync(CancellationToken ct = default)
    {
        var seed = opcoes.Value;
        if (!seed.Habilitado)
        {
            return;
        }

        if (await usuarios.Users.AnyAsync(ct))
        {
            log.LogInformation("Seed ignorado: já existem usuários cadastrados.");
            return;
        }

        if (string.IsNullOrWhiteSpace(seed.GestorEmail) || string.IsNullOrWhiteSpace(seed.GestorSenha))
        {
            throw new InvalidOperationException(
                "Seed:Habilitado está ligado, mas Seed:GestorEmail e/ou Seed:GestorSenha não foram configurados " +
                "(use 'dotnet user-secrets').");
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        var email = seed.GestorEmail.Trim();
        var gestor = new Usuario
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Nome = string.IsNullOrWhiteSpace(seed.GestorNome) ? "Gestor" : seed.GestorNome.Trim(),
            Perfil = PerfilUsuario.Gestor,
            Ativo = true,
            CriadoEm = agora,
            AtualizadoEm = agora
        };

        var resultado = await usuarios.CreateAsync(gestor, seed.GestorSenha);
        if (!resultado.Succeeded)
        {
            // Só os códigos de erro do Identity (ex.: PasswordTooShort) — nunca a senha nem o e-mail.
            var codigos = string.Join(", ", resultado.Errors.Select(e => e.Code));
            throw new InvalidOperationException($"Não foi possível criar o primeiro gestor: {codigos}.");
        }

        log.LogInformation("Primeiro gestor criado (id {UsuarioId}).", gestor.Id);
    }
}
