using System.Text.RegularExpressions;
using Jvd.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Jvd.Infrastructure.Persistencia;

/// <summary>
/// Contexto do banco. Usa <see cref="IdentityUserContext{TUser,TKey}"/> (sem tabelas de roles):
/// o perfil do usuário é a coluna <c>perfil</c>, fonte única de permissão.
/// </summary>
public sealed class JvdDbContext(DbContextOptions<JvdDbContext> options)
    : IdentityUserContext<Usuario, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Usuario>(e =>
        {
            e.ToTable("usuario");
            e.Property(u => u.Nome).HasMaxLength(150).IsRequired();
            e.Property(u => u.Perfil).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(u => u.Cpf).HasMaxLength(11);
            e.Property(u => u.CriadoEm).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(u => u.AtualizadoEm).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()");

            // Unicidade do e-mail garantida pelo próprio banco (não só pela regra da aplicação). Reaproveita o
            // índice "EmailIndex" do Identity, tornando-o único (auditoria P3-3).
            e.HasIndex(u => u.NormalizedEmail).IsUnique();
        });

        builder.Entity<IdentityUserClaim<Guid>>().ToTable("usuario_claim");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("usuario_login");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("usuario_token");

        builder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_token");
            e.HasKey(t => t.Id);
            e.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(t => t.CriadoEm).HasColumnType("datetime2");
            e.Property(t => t.ExpiraEm).HasColumnType("datetime2");
            e.Property(t => t.UsadoEm).HasColumnType("datetime2");
            e.Property(t => t.RevogadoEm).HasColumnType("datetime2");
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UsuarioId);
            // Usuários nunca são apagados fisicamente (exclusão lógica), então não há cascata.
            e.HasOne<Usuario>().WithMany().HasForeignKey(t => t.UsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        AplicarSnakeCase(builder);
    }

    /// <summary>Convenção do projeto: colunas em snake_case (Database/MODELO_DADOS.md).</summary>
    private static void AplicarSnakeCase(ModelBuilder builder)
    {
        foreach (var entidade in builder.Model.GetEntityTypes())
        {
            foreach (var propriedade in entidade.GetProperties())
            {
                propriedade.SetColumnName(ParaSnakeCase(propriedade.Name));
            }
        }
    }

    private static string ParaSnakeCase(string nome) =>
        Regex.Replace(nome, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
