using System.Text.RegularExpressions;
using Jvd.Domain;
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
    public DbSet<EventoDeCadastro> EventosDeCadastro => Set<EventoDeCadastro>();
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Equipamento> Equipamentos => Set<Equipamento>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Usuario>(e =>
        {
            e.ToTable("usuario");
            e.Property(u => u.Nome).HasMaxLength(150).IsRequired();
            e.Property(u => u.Perfil).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(u => u.Cpf).HasMaxLength(11);
            e.Property(u => u.DeveTrocarSenha).HasDefaultValue(false);
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

        builder.Entity<EventoDeCadastro>(e =>
        {
            e.ToTable("evento_cadastro");
            e.HasKey(x => x.Id);
            e.Property(x => x.Entidade).HasMaxLength(40).IsRequired();
            e.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.OcorridoEm).HasColumnType("datetime2");
            e.Property(x => x.Campos).HasMaxLength(500);
            e.HasIndex(x => new { x.Entidade, x.EntidadeId, x.OcorridoEm });
        });

        builder.Entity<Cliente>(e =>
        {
            e.ToTable("cliente");
            e.HasKey(x => x.Id);
            e.Property(x => x.NomeRazaoSocial).HasMaxLength(200).IsRequired();
            e.Property(x => x.NomeFantasia).HasMaxLength(200);
            e.Property(x => x.Documento).HasMaxLength(14);
            e.Property(x => x.InscricaoEstadualRg).HasMaxLength(30);
            e.Property(x => x.Logradouro).HasMaxLength(150);
            e.Property(x => x.Numero).HasMaxLength(20);
            e.Property(x => x.Bairro).HasMaxLength(80);
            e.Property(x => x.Cep).HasMaxLength(8);
            e.Property(x => x.Cidade).HasMaxLength(80);
            e.Property(x => x.Uf).HasMaxLength(2);
            e.Property(x => x.Telefone).HasMaxLength(30);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Origem).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.CriadoEm).HasColumnType("datetime2");
            e.Property(x => x.AtualizadoEm).HasColumnType("datetime2");
            e.Property(x => x.Versao).IsRowVersion();
            // Documento NÃO é único (filiais compartilham CNPJ): o índice só acelera a busca e o aviso de duplicidade.
            e.HasIndex(x => x.Documento);
            e.HasIndex(x => x.NomeRazaoSocial);
            e.HasIndex(x => x.Ativo);
        });

        builder.Entity<Equipamento>(e =>
        {
            e.ToTable("equipamento");
            e.HasKey(x => x.Id);
            e.Property(x => x.Descricao).HasMaxLength(200).IsRequired();
            e.Property(x => x.NumeroSerie).HasMaxLength(60);
            e.Property(x => x.NumeroInmetro).HasMaxLength(40);
            e.Property(x => x.Observacoes).HasMaxLength(1000);
            e.Property(x => x.CriadoEm).HasColumnType("datetime2");
            e.Property(x => x.AtualizadoEm).HasColumnType("datetime2");
            e.Property(x => x.Versao).IsRowVersion();
            e.HasIndex(x => x.ClienteId);
            e.HasIndex(x => x.NumeroSerie);
            e.HasIndex(x => x.NumeroInmetro);
            // Cliente nunca é apagado fisicamente (desativação lógica): sem cascata.
            e.HasOne<Cliente>().WithMany().HasForeignKey(x => x.ClienteId).OnDelete(DeleteBehavior.Restrict);
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
