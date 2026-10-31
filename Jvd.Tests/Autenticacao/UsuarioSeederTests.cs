using Jvd.Domain;
using Jvd.Infrastructure;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Tests.Suporte;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Autenticacao;

/// <summary>
/// Cada teste usa o seu próprio banco descartável, porque o seed depende de "não existir nenhum usuário".
/// Roda só com JVD_TESTES_BANCO=1.
/// </summary>
public class UsuarioSeederTests
{
    private const string EmailSemente = "gestor-semente@exemplo.invalid";

    private static ServiceProvider Provedor(BancoDeTeste banco, bool habilitado, string senha = DadosDeTeste.SenhaValida)
    {
        var valores = DadosDeTeste.ConfiguracaoBase(banco.Conexao);
        valores["Seed:Habilitado"] = habilitado ? "true" : "false";
        valores["Seed:GestorNome"] = "Gestor Semente";
        valores["Seed:GestorEmail"] = EmailSemente;
        valores["Seed:GestorSenha"] = senha;

        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuracao)
            .AddJvdInfrastructure(configuracao)
            .BuildServiceProvider();
    }

    [FactBanco]
    public async Task Executar_SemUsuarios_CriaOGestorComPerfilEPoliticaDeSenha()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        await using var provedor = Provedor(banco, habilitado: true);

        await using (var escopo = provedor.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<UsuarioSeeder>().ExecutarAsync();
        }

        await using var verificacao = provedor.CreateAsyncScope();
        var usuarios = verificacao.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var gestor = await usuarios.FindByEmailAsync(EmailSemente);
        Assert.NotNull(gestor);
        Assert.Equal(PerfilUsuario.Gestor, gestor.Perfil);
        Assert.True(gestor.Ativo);
        Assert.True(await usuarios.CheckPasswordAsync(gestor, DadosDeTeste.SenhaValida));
        Assert.NotEqual(DadosDeTeste.SenhaValida, gestor.PasswordHash); // senha só como hash
    }

    [FactBanco]
    public async Task Executar_ComUsuariosExistentes_NaoCriaNada()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        await using var provedor = Provedor(banco, habilitado: true);

        await using (var escopo = provedor.CreateAsyncScope())
        {
            var usuarios = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            await usuarios.CreateAsync(new Usuario
            {
                Id = Guid.NewGuid(), UserName = "outro@exemplo.invalid", Email = "outro@exemplo.invalid",
                Nome = "Outro", Perfil = PerfilUsuario.Tecnico
            }, DadosDeTeste.SenhaValida);
        }

        await using (var escopo = provedor.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<UsuarioSeeder>().ExecutarAsync();
        }

        await using var db = banco.CriarContexto();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Null(await db.Users.SingleOrDefaultAsync(u => u.Email == EmailSemente));
    }

    [FactBanco]
    public async Task Executar_Desabilitado_NaoFazNada()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        await using var provedor = Provedor(banco, habilitado: false);

        await using (var escopo = provedor.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<UsuarioSeeder>().ExecutarAsync();
        }

        await using var db = banco.CriarContexto();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [FactBanco]
    public async Task Executar_ComSenhaFraca_FalhaSemVazarASenhaNemOEmail()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        const string senhaFraca = "abc123";
        await using var provedor = Provedor(banco, habilitado: true, senha: senhaFraca);
        await using var escopo = provedor.CreateAsyncScope();

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            escopo.ServiceProvider.GetRequiredService<UsuarioSeeder>().ExecutarAsync());

        Assert.Contains("PasswordTooShort", erro.Message);
        Assert.DoesNotContain(senhaFraca, erro.Message);
        Assert.DoesNotContain(EmailSemente, erro.Message);

        await using var db = banco.CriarContexto();
        Assert.Equal(0, await db.Users.CountAsync());
    }
}
