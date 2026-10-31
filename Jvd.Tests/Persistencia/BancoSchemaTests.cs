using Jvd.Domain;
using Jvd.Infrastructure.Identity;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace Jvd.Tests.Persistencia;

/// <summary>Garantias que o próprio SQL Server deve dar (não só a aplicação). Roda só com JVD_TESTES_BANCO=1.</summary>
public class BancoSchemaTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static Usuario NovoUsuario(string email, string userName) => new()
    {
        Id = Guid.NewGuid(),
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        Nome = "Teste de schema",
        Perfil = PerfilUsuario.Tecnico,
        CriadoEm = DateTime.UtcNow,
        AtualizadoEm = DateTime.UtcNow
    };

    [FactBanco]
    public async Task Banco_ImpedeEmailDuplicado_MesmoComUserNameDiferente()
    {
        var email = DadosDeTeste.NovoEmail();
        await using (var db = banco.CriarContexto())
        {
            db.Users.Add(NovoUsuario(email, userName: "nome-a-" + Guid.NewGuid().ToString("N")));
            await db.SaveChangesAsync();
        }

        // O Identity só garante e-mail único se UserName == Email. O banco precisa garantir sozinho.
        await using var segundo = banco.CriarContexto();
        segundo.Users.Add(NovoUsuario(email, userName: "nome-b-" + Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<DbUpdateException>(() => segundo.SaveChangesAsync());
    }

    [FactBanco]
    public async Task Banco_AceitaVariosUsuariosSemEmail()
    {
        // O índice único é filtrado (NormalizedEmail IS NOT NULL): ausência de e-mail não colide.
        await using var db = banco.CriarContexto();
        foreach (var nome in new[] { "sem-email-1-", "sem-email-2-" })
        {
            var usuario = NovoUsuario("x@x.invalid", nome + Guid.NewGuid().ToString("N"));
            usuario.Email = null;
            usuario.NormalizedEmail = null;
            db.Users.Add(usuario);
        }

        await db.SaveChangesAsync();
    }

    [FactBanco]
    public async Task Banco_RefreshToken_ExigeUsuarioExistente()
    {
        await using var db = banco.CriarContexto();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UsuarioId = Guid.NewGuid(), // usuário inexistente
            TokenHash = new string('a', 64),
            CriadoEm = DateTime.UtcNow,
            ExpiraEm = DateTime.UtcNow.AddDays(1)
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [FactBanco]
    public async Task Banco_RefreshToken_NaoAceitaHashDuplicado()
    {
        Guid usuarioId;
        await using (var db = banco.CriarContexto())
        {
            var usuario = NovoUsuario(DadosDeTeste.NovoEmail(), "u-" + Guid.NewGuid().ToString("N"));
            db.Users.Add(usuario);
            await db.SaveChangesAsync();
            usuarioId = usuario.Id;
        }

        RefreshToken Novo() => new()
        {
            Id = Guid.NewGuid(), UsuarioId = usuarioId, TokenHash = new string('b', 64),
            CriadoEm = DateTime.UtcNow, ExpiraEm = DateTime.UtcNow.AddDays(1)
        };

        await using (var primeiro = banco.CriarContexto())
        {
            primeiro.RefreshTokens.Add(Novo());
            await primeiro.SaveChangesAsync();
        }

        await using var segundo = banco.CriarContexto();
        segundo.RefreshTokens.Add(Novo());
        await Assert.ThrowsAsync<DbUpdateException>(() => segundo.SaveChangesAsync());
    }
}
