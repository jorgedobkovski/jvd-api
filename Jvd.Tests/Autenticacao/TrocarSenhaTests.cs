using Jvd.Application.Autenticacao;
using Jvd.Application.Comum;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Jvd.Tests.Autenticacao;

/// <summary>Troca da própria senha e fluxo da senha provisória (ADR-006). SQL Server real; só com JVD_TESTES_BANCO=1.</summary>
public class TrocarSenhaTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _sp = null!;

    public TrocarSenhaTests(BancoDeTeste banco) => _banco = banco;

    public Task InitializeAsync()
    {
        _sp = ServicosDeTeste.Construir(_banco.Conexao, _relogio);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

    private async Task<T> Auth<T>(Func<IAutenticacaoService, Task<T>> acao)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IAutenticacaoService>());
    }

    private Task<ResultadoAutenticacao> LoginAsync(string email, string senha) =>
        Auth(s => s.LoginAsync(new LoginComando(email, senha)));

    private Task<Resultado<TokensEmitidos>> TrocarAsync(Guid id, string atual, string nova) =>
        Auth(s => s.TrocarSenhaAsync(id, atual, nova));

    private async Task<(Guid Id, string Email)> UsuarioComSenhaProvisoriaAsync() =>
        await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico, deveTrocarSenha: true, senha: ServicosDeTeste.SenhaProvisoria);

    private async Task<List<RefreshToken>> TokensAsync(Guid id)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<JvdDbContext>()
            .RefreshTokens.AsNoTracking().Where(t => t.UsuarioId == id).ToListAsync();
    }

    [FactBanco]
    public async Task SenhaProvisoria_LoginInformaATrocaEOTokenCarregaAClaim()
    {
        var (id, email) = await UsuarioComSenhaProvisoriaAsync();

        var login = await LoginAsync(email, ServicosDeTeste.SenhaProvisoria);

        Assert.True(login.Sucesso);
        Assert.True(login.Tokens!.Usuario.DeveTrocarSenha);
        Assert.True(new JsonWebToken(login.Tokens.AccessToken).TryGetClaim(NomesDeClaims.TrocarSenha, out _));
        Assert.Equal(id, login.Tokens.Usuario.Id);
    }

    [FactBanco]
    public async Task TrocarSenha_ComDadosValidos_LimpaAObrigacaoRevogaSessoesEEmiteTokensNovos()
    {
        var (id, email) = await UsuarioComSenhaProvisoriaAsync();
        var antigos = new[]
        {
            (await LoginAsync(email, ServicosDeTeste.SenhaProvisoria)).Tokens!,
            (await LoginAsync(email, ServicosDeTeste.SenhaProvisoria)).Tokens!
        };

        var troca = await TrocarAsync(id, ServicosDeTeste.SenhaProvisoria, ServicosDeTeste.SenhaNova);

        Assert.True(troca.Sucesso);
        var novos = troca.Valor!;
        Assert.False(novos.Usuario.DeveTrocarSenha);
        Assert.False(new JsonWebToken(novos.AccessToken).TryGetClaim(NomesDeClaims.TrocarSenha, out _)); // token liberado

        // sessões antigas não renovam mais; a nova sim
        foreach (var antigo in antigos)
        {
            Assert.False((await Auth(s => s.RenovarAsync(antigo.RefreshToken))).Sucesso);
        }
        Assert.True((await Auth(s => s.RenovarAsync(novos.RefreshToken))).Sucesso);

        // a senha antiga morreu e a nova funciona, já sem obrigação de troca
        Assert.False((await LoginAsync(email, ServicosDeTeste.SenhaProvisoria)).Sucesso);
        var relogin = await LoginAsync(email, ServicosDeTeste.SenhaNova);
        Assert.True(relogin.Sucesso);
        Assert.False(relogin.Tokens!.Usuario.DeveTrocarSenha);

        await using var escopo = _sp.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        var evento = await db.EventosDeCadastro.AsNoTracking().SingleAsync(e => e.EntidadeId == id && e.Tipo == TipoEventoCadastro.SenhaTrocada);
        Assert.Equal(id, evento.AtorId);
        Assert.Equal("senha", evento.Campos);
    }

    [FactBanco]
    public async Task TrocarSenha_ComSenhaAtualErrada_FalhaEContaComoTentativa()
    {
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_sp);

        for (var i = 0; i < 5; i++)
        {
            var erro = await TrocarAsync(id, "SenhaErrada#999", ServicosDeTeste.SenhaNova);
            Assert.Equal(CodigosDeErro.SenhaAtualIncorreta, erro.Erros[0].Codigo);
        }

        // 5 erros bloquearam a conta: nem a senha certa entra (quem roubou só o access token não adivinha à vontade)
        Assert.False((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);
        var bloqueado = await TrocarAsync(id, DadosDeTeste.SenhaValida, ServicosDeTeste.SenhaNova);
        Assert.Equal(CodigosDeErro.SessaoInvalida, bloqueado.Erros[0].Codigo);
    }

    [FactBanco]
    public async Task TrocarSenha_ParaAMesmaSenha_EhRecusada()
    {
        var (id, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp);

        var resultado = await TrocarAsync(id, DadosDeTeste.SenhaValida, DadosDeTeste.SenhaValida);

        Assert.Equal(CodigosDeErro.NovaSenhaIgualAtual, resultado.Erros[0].Codigo);
    }

    [FactBanco]
    public async Task TrocarSenha_ComNovaSenhaFraca_FalhaSemEcoarEMantemTudoComoEstava()
    {
        var (id, email) = await UsuarioComSenhaProvisoriaAsync();

        var resultado = await TrocarAsync(id, ServicosDeTeste.SenhaProvisoria, "fraca");

        Assert.False(resultado.Sucesso);
        Assert.All(resultado.Erros, e => Assert.Equal(CodigosDeErro.SenhaInvalida, e.Codigo));
        Assert.All(resultado.Erros, e => Assert.DoesNotContain("fraca", e.Mensagem));

        var login = await LoginAsync(email, ServicosDeTeste.SenhaProvisoria);
        Assert.True(login.Sucesso);                                 // senha antiga segue valendo
        Assert.True(login.Tokens!.Usuario.DeveTrocarSenha);          // e a obrigação continua
    }

    [FactBanco]
    public async Task TrocarSenha_UsuarioInativoOuInexistente_RetornaSessaoInvalida()
    {
        var (inativo, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, ativo: false);

        Assert.Equal(CodigosDeErro.SessaoInvalida, (await TrocarAsync(inativo, DadosDeTeste.SenhaValida, ServicosDeTeste.SenhaNova)).Erros[0].Codigo);
        Assert.Equal(CodigosDeErro.SessaoInvalida, (await TrocarAsync(Guid.NewGuid(), "x", "y")).Erros[0].Codigo);
    }

    [FactBanco]
    public async Task TrocarSenha_DeUmUsuarioNaoMexeNasSessoesDeOutro()
    {
        var (idA, emailA) = await ServicosDeTeste.CriarUsuarioAsync(_sp);
        var (idB, emailB) = await ServicosDeTeste.CriarUsuarioAsync(_sp);
        await LoginAsync(emailA, DadosDeTeste.SenhaValida);
        var tokensB = (await LoginAsync(emailB, DadosDeTeste.SenhaValida)).Tokens!;

        await TrocarAsync(idA, DadosDeTeste.SenhaValida, ServicosDeTeste.SenhaNova);

        Assert.All(await TokensAsync(idB), t => Assert.Null(t.RevogadoEm));
        Assert.True((await Auth(s => s.RenovarAsync(tokensB.RefreshToken))).Sucesso);
    }
}
