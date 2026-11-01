using System.Text.RegularExpressions;
using Jvd.Domain;
using Jvd.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ImportacaoRealizada = Jvd.Domain.Importacao; // "Importacao" também é nome de namespace da Infrastructure

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
    public DbSet<Emissor> Emissores => Set<Emissor>();
    public DbSet<ServicoCatalogo> Catalogo => Set<ServicoCatalogo>();
    public DbSet<ImportacaoRealizada> Importacoes => Set<ImportacaoRealizada>();
    public DbSet<ImportacaoErro> ImportacoesErros => Set<ImportacaoErro>();
    public DbSet<Relatorio> Relatorios => Set<Relatorio>();
    public DbSet<RelatorioEvento> Eventos => Set<RelatorioEvento>();
    public DbSet<ContadorDeNumeracao> Contadores => Set<ContadorDeNumeracao>();
    public DbSet<RelatorioRevisao> Revisoes => Set<RelatorioRevisao>();

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

        builder.Entity<Emissor>(e =>
        {
            e.ToTable("emissor");
            e.HasKey(x => x.Id);
            // Id fixo, sem geração de valor: a chave primária garante que só exista um emissor.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RazaoSocial).HasMaxLength(200).IsRequired();
            e.Property(x => x.NomeFantasia).HasMaxLength(200);
            e.Property(x => x.Cnpj).HasMaxLength(14).IsRequired();
            e.Property(x => x.InscricaoEstadual).HasMaxLength(30);
            e.Property(x => x.Logradouro).HasMaxLength(150);
            e.Property(x => x.Numero).HasMaxLength(20);
            e.Property(x => x.Bairro).HasMaxLength(80);
            e.Property(x => x.Cep).HasMaxLength(8);
            e.Property(x => x.Cidade).HasMaxLength(80);
            e.Property(x => x.Uf).HasMaxLength(2);
            e.Property(x => x.Telefones).HasMaxLength(200);
            e.Property(x => x.NumeroAutorizacaoIpem).HasMaxLength(60);
            e.Property(x => x.ResponsavelTecnicoNome).HasMaxLength(150);
            e.Property(x => x.ResponsavelTecnicoCpf).HasMaxLength(11);
            e.Property(x => x.AtualizadoEm).HasColumnType("datetime2");
            e.Property(x => x.Versao).IsRowVersion();
        });

        builder.Entity<ServicoCatalogo>(e =>
        {
            e.ToTable("catalogo_servico");
            e.HasKey(x => x.Id);
            e.Property(x => x.CodigoOrigem).HasMaxLength(40);
            e.Property(x => x.Descricao).HasMaxLength(200).IsRequired();
            e.Property(x => x.PrecoSugerido).HasColumnType("decimal(12,2)");
            e.Property(x => x.CriadoEm).HasColumnType("datetime2");
            e.Property(x => x.AtualizadoEm).HasColumnType("datetime2");
            e.Property(x => x.Versao).IsRowVersion();
            e.HasIndex(x => x.Descricao);
            e.HasIndex(x => x.Ativo);
        });

        builder.Entity<ImportacaoRealizada>(e =>
        {
            e.ToTable("importacao");
            e.HasKey(x => x.Id);
            e.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.NomeArquivo).HasMaxLength(200).IsRequired();
            e.Property(x => x.HashArquivo).HasMaxLength(64).IsRequired();
            e.Property(x => x.ExecutadaEm).HasColumnType("datetime2");
            // Mesmo arquivo, mesmo tipo: uma única vez (trava contra reimportação, inclusive entre pedidos simultâneos).
            e.HasIndex(x => new { x.Tipo, x.HashArquivo }).IsUnique();
            e.HasIndex(x => x.ExecutadaEm);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ImportacaoErro>(e =>
        {
            e.ToTable("importacao_erro");
            e.HasKey(x => x.Id);
            e.Property(x => x.Gravidade).HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.Motivo).HasMaxLength(300).IsRequired();
            e.HasIndex(x => new { x.ImportacaoId, x.Linha });
            e.HasOne<ImportacaoRealizada>().WithMany().HasForeignKey(x => x.ImportacaoId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Relatorio>(e =>
        {
            e.ToTable("relatorio", t =>
            {
                t.HasCheckConstraint("ck_relatorio_total_geral", "[total_geral] = [total] - [desconto]");
                t.HasCheckConstraint("ck_relatorio_desconto", "[desconto] >= 0 AND [desconto] <= [total]");
                t.HasCheckConstraint("ck_relatorio_garantia", "[garantia_dias] >= 0 AND [garantia_dias] <= 9999");
                t.HasCheckConstraint("ck_relatorio_revisao", "[revisao] >= 0");
                // Cancelado sempre tem quando e motivo; os demais nunca têm.
                t.HasCheckConstraint("ck_relatorio_cancelamento",
                    "([status] = 'Cancelado' AND [cancelado_em] IS NOT NULL AND [cancelado_por] IS NOT NULL AND [motivo_cancelamento] IS NOT NULL) "
                    + "OR ([status] <> 'Cancelado' AND [cancelado_em] IS NULL AND [cancelado_por] IS NULL AND [motivo_cancelamento] IS NULL)");
                // Rascunho não tem número; emitido e cancelado sempre têm.
                t.HasCheckConstraint("ck_relatorio_numero",
                    "([status] = 'Rascunho' AND [numero] IS NULL) OR ([status] <> 'Rascunho' AND [numero] IS NOT NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.TipoServico).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.DataServico).HasColumnType("date");
            e.Property(x => x.ServicoExecutado).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Observacoes).HasMaxLength(2000);
            e.Property(x => x.VerificacaoAnteriorData).HasColumnType("date");
            e.Property(x => x.VerificacaoAnteriorCertificado).HasMaxLength(60);
            e.Property(x => x.NumeroSeloReparador).HasMaxLength(60);
            e.Property(x => x.Total).HasColumnType("decimal(12,2)");
            e.Property(x => x.Desconto).HasColumnType("decimal(12,2)");
            e.Property(x => x.TotalGeral).HasColumnType("decimal(12,2)");
            e.Property(x => x.ClienteSnapshot).HasColumnType("nvarchar(max)");
            e.Property(x => x.EquipamentoSnapshot).HasColumnType("nvarchar(max)");
            e.Property(x => x.EmissorSnapshot).HasColumnType("nvarchar(max)");
            e.Property(x => x.MotivoCancelamento).HasMaxLength(500);
            e.Property(x => x.EmitidoEm).HasColumnType("datetime2");
            e.Property(x => x.CanceladoEm).HasColumnType("datetime2");
            e.Property(x => x.CriadoEm).HasColumnType("datetime2");
            e.Property(x => x.AtualizadoEm).HasColumnType("datetime2");
            e.Property(x => x.Versao).IsRowVersion();

            // O número é único (sem repetir nem reaproveitar); rascunhos não têm número.
            e.HasIndex(x => x.Numero).IsUnique().HasFilter("[numero] IS NOT NULL");
            e.HasIndex(x => new { x.ClienteId, x.DataServico });
            e.HasIndex(x => new { x.EquipamentoId, x.DataServico });
            e.HasIndex(x => new { x.TecnicoId, x.DataServico });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.DataServico);

            // Nada é apagado fisicamente (exceto rascunho, que é descartado pelo serviço): sem cascata.
            e.HasOne<Cliente>().WithMany().HasForeignKey(x => x.ClienteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Equipamento>().WithMany().HasForeignKey(x => x.EquipamentoId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.TecnicoId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.CriadoPor).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ServicoCatalogo>().WithMany().HasForeignKey(x => x.CatalogoServicoId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RelatorioEvento>(e =>
        {
            e.ToTable("relatorio_evento");
            e.HasKey(x => x.Id);
            e.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.OcorridoEm).HasColumnType("datetime2");
            e.Property(x => x.Detalhe).HasMaxLength(300);
            e.HasIndex(x => new { x.RelatorioId, x.OcorridoEm });
            e.HasOne<Relatorio>().WithMany().HasForeignKey(x => x.RelatorioId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RelatorioRevisao>(e =>
        {
            e.ToTable("relatorio_revisao", t => t.HasCheckConstraint("ck_relatorio_revisao_numero", "[revisao] >= 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.RetificadoEm).HasColumnType("datetime2");
            e.Property(x => x.Motivo).HasMaxLength(500).IsRequired();
            e.Property(x => x.DadosAnteriores).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(x => x.DadosNovos).HasColumnType("nvarchar(max)").IsRequired();
            e.HasIndex(x => new { x.RelatorioId, x.Revisao }).IsUnique();
            e.HasOne<Relatorio>().WithMany().HasForeignKey(x => x.RelatorioId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.RetificadoPor).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ContadorDeNumeracao>(e =>
        {
            e.ToTable("contador_numeracao");
            e.HasKey(x => x.Chave);
            e.Property(x => x.Chave).HasMaxLength(40).ValueGeneratedNever();
            e.HasData(new ContadorDeNumeracao { Chave = ContadorDeNumeracao.ChaveDoRelatorio, Ultimo = 0 });
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
