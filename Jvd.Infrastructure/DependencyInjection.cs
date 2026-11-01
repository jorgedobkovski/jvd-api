using Jvd.Application.Autenticacao;
using Jvd.Application.Catalogo;
using Jvd.Application.Clientes;
using Jvd.Application.Emissor;
using Jvd.Application.Importacao;
using Jvd.Application.Relatorios;
using Jvd.Application.Equipamentos;
using Jvd.Application.Usuarios;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Cadastros;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Importacao;
using Jvd.Infrastructure.Relatorios;
using Jvd.Infrastructure.Persistencia;
using Jvd.Infrastructure.Usuarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Jvd.Infrastructure;

public static class DependencyInjection
{
    public const string NomeDaConexao = "JvdSistema";

    public static IServiceCollection AddJvdInfrastructure(this IServiceCollection services, IConfiguration configuracao)
    {
        // A conexão é lida só quando o contexto é criado, a partir da configuração FINAL. Assim os testes
        // conseguem sobrescrevê-la e nunca tocam o banco de desenvolvimento por acidente.
        services.AddDbContext<JvdDbContext>((sp, o) =>
        {
            var conexao = sp.GetRequiredService<IConfiguration>().GetConnectionString(NomeDaConexao)
                ?? throw new InvalidOperationException($"ConnectionStrings:{NomeDaConexao} não está configurada.");
            o.UseSqlServer(conexao);

            // Interceptors registrados no DI (usados nos testes para simular falhas de gravação).
            o.AddInterceptors(sp.GetServices<IInterceptor>());
        });

        services
            .AddIdentityCore<Usuario>(o =>
            {
                o.User.RequireUniqueEmail = true;

                // Política de senha proposta (JvdApi/DECISIONS.md).
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;

                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<JvdDbContext>();

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<JwtOptions>().Bind(configuracao.GetSection(JwtOptions.Secao)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<SeedOptions>().Bind(configuracao.GetSection(SeedOptions.Secao));

        services.AddSingleton<EmissorDeTokens>();
        services.AddScoped<IAutenticacaoService, AutenticacaoService>();
        services.AddScoped<IUsuarioService, UsuarioService>();
        services.AddScoped<IClienteService, ClienteService>();
        services.AddScoped<IEquipamentoService, EquipamentoService>();
        services.AddScoped<IEmissorService, EmissorService>();
        services.AddScoped<ICatalogoServicoService, CatalogoServicoService>();
        services.AddSingleton<ILeitorDePlanilha, LeitorXlsx>();
        services.AddScoped<IImportacaoService, ImportacaoService>();
        services.AddScoped<IRelatorioService, RelatorioService>();
        services.AddScoped<IRelatorioPdfService, RelatorioPdfService>();
        services.AddScoped<UsuarioSeeder>();

        return services;
    }
}
