using Jvd.Domain;
using Jvd.Infrastructure;
using Jvd.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Suporte;

/// <summary>Monta o contêiner de serviços real (sem a API HTTP) sobre um banco descartável.</summary>
public static class ServicosDeTeste
{
    public const string SenhaProvisoria = "Provisoria#2026";
    public const string SenhaNova = "NovaSenha#2026ok";

    public static ServiceProvider Construir(
        string conexao, TimeProvider relogio, IDictionary<string, string?>? sobrescritas = null)
    {
        var valores = DadosDeTeste.ConfiguracaoBase(conexao);
        if (sobrescritas is not null)
        {
            foreach (var par in sobrescritas) valores[par.Key] = par.Value;
        }

        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuracao)
            .AddSingleton(relogio)
            .AddJvdInfrastructure(configuracao)
            .BuildServiceProvider();
    }

    /// <summary>Cria um usuário direto pelo Identity (sem passar pelas regras do serviço), para montar cenários.</summary>
    public static async Task<(Guid Id, string Email)> CriarUsuarioAsync(
        IServiceProvider provedor,
        PerfilUsuario perfil = PerfilUsuario.Tecnico,
        bool ativo = true,
        bool deveTrocarSenha = false,
        string senha = DadosDeTeste.SenhaValida,
        string nome = "Usuário de Teste")
    {
        await using var escopo = provedor.CreateAsyncScope();
        var usuarios = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var relogio = escopo.ServiceProvider.GetRequiredService<TimeProvider>();
        var email = DadosDeTeste.NovoEmail();
        var agora = relogio.GetUtcNow().UtcDateTime;

        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Nome = nome,
            Perfil = perfil,
            Ativo = ativo,
            DeveTrocarSenha = deveTrocarSenha,
            CriadoEm = agora,
            AtualizadoEm = agora
        };

        var resultado = await usuarios.CreateAsync(usuario, senha);
        if (!resultado.Succeeded)
        {
            throw new InvalidOperationException("Falha ao montar o cenário: " + string.Join(",", resultado.Errors.Select(e => e.Code)));
        }

        return (usuario.Id, email);
    }
}
