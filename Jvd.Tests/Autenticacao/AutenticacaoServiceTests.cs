using Jvd.Application.Autenticacao;
using Jvd.Domain;
using Jvd.Infrastructure;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Autenticacao;

/// <summary>Usa SQL Server real (banco descartável). Roda só com JVD_TESTES_BANCO=1.</summary>
public class AutenticacaoServiceTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provedor = null!;

    public AutenticacaoServiceTests(BancoDeTeste banco) => _banco = banco;

    private readonly HasherContador _hasher = new();
    private readonly InterceptorDeFalha _falha = new();

    public Task InitializeAsync()
    {
        _provedor = ConstruirProvedor();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provedor.DisposeAsync();

    private ServiceProvider ConstruirProvedor(IDictionary<string, string?>? sobrescritas = null)
    {
        var valores = DadosDeTeste.ConfiguracaoBase(_banco.Conexao);
        if (sobrescritas is not null)
        {
            foreach (var par in sobrescritas) valores[par.Key] = par.Value;
        }

        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuracao)
            .AddSingleton<TimeProvider>(_relogio)
            .AddSingleton<IPasswordHasher<Usuario>>(_hasher)     // conta as verificações de hash (P2-1)
            .AddSingleton<IInterceptor>(_falha)                   // simula falha ao gravar (P1-1)
            .AddJvdInfrastructure(configuracao)
            .BuildServiceProvider();
    }

    /// <summary>Hasher normal, mas conta quantas vezes uma senha foi verificada.</summary>
    private sealed class HasherContador : PasswordHasher<Usuario>
    {
        private int _verificacoes;
        public int Verificacoes => Volatile.Read(ref _verificacoes);

        public override PasswordVerificationResult VerifyHashedPassword(Usuario usuario, string hash, string senha)
        {
            Interlocked.Increment(ref _verificacoes);
            return base.VerifyHashedPassword(usuario, hash, senha);
        }
    }

    /// <summary>Quando armado, faz a gravação de um novo RefreshToken falhar (como uma queda de conexão).</summary>
    private sealed class InterceptorDeFalha : SaveChangesInterceptor
    {
        public volatile bool Armado;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> resultado, CancellationToken ct = default)
        {
            if (Armado && eventData.Context!.ChangeTracker.Entries<RefreshToken>().Any(e => e.State == EntityState.Added))
            {
                throw new TimeoutException("falha simulada ao gravar o novo refresh token");
            }

            return base.SavingChangesAsync(eventData, resultado, ct);
        }
    }

    // ---------- apoio ----------

    private async Task<(Guid Id, string Email)> CriarUsuarioAsync(
        PerfilUsuario perfil = PerfilUsuario.Tecnico, bool ativo = true)
    {
        await using var escopo = _provedor.CreateAsyncScope();
        var usuarios = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var email = DadosDeTeste.NovoEmail();
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            Nome = "Usuário de Teste",
            Perfil = perfil,
            Ativo = ativo,
            CriadoEm = _relogio.GetUtcNow().UtcDateTime,
            AtualizadoEm = _relogio.GetUtcNow().UtcDateTime
        };
        var resultado = await usuarios.CreateAsync(usuario, DadosDeTeste.SenhaValida);
        Assert.True(resultado.Succeeded, string.Join(",", resultado.Errors.Select(e => e.Code)));
        return (usuario.Id, email);
    }

    private async Task<T> ComServicoAsync<T>(Func<IAutenticacaoService, Task<T>> acao)
    {
        await using var escopo = _provedor.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IAutenticacaoService>());
    }

    private Task<ResultadoAutenticacao> LoginAsync(string email, string senha = DadosDeTeste.SenhaValida) =>
        ComServicoAsync(s => s.LoginAsync(new LoginComando(email, senha)));

    private Task<ResultadoAutenticacao> RenovarAsync(string refresh) =>
        ComServicoAsync(s => s.RenovarAsync(refresh));

    private async Task<List<RefreshToken>> TokensDoUsuarioAsync(Guid usuarioId)
    {
        await using var escopo = _provedor.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        return await db.RefreshTokens.AsNoTracking().Where(t => t.UsuarioId == usuarioId).ToListAsync();
    }

    // ---------- login ----------

    [FactBanco]
    public async Task Login_ComCredenciaisValidas_EmiteTokensEGravaApenasOHash()
    {
        var (id, email) = await CriarUsuarioAsync(PerfilUsuario.Gestor);

        var resultado = await LoginAsync(email);

        Assert.True(resultado.Sucesso);
        var tokens = resultado.Tokens!;
        Assert.Equal(id, tokens.Usuario.Id);
        Assert.Equal(PerfilUsuario.Gestor, tokens.Usuario.Perfil);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));

        var gravado = Assert.Single(await TokensDoUsuarioAsync(id));
        Assert.NotEqual(tokens.RefreshToken, gravado.TokenHash);
        Assert.Equal(EmissorDeTokens.CalcularHash(tokens.RefreshToken), gravado.TokenHash);
        Assert.Equal(64, gravado.TokenHash.Length);
        Assert.Equal(_relogio.GetUtcNow().UtcDateTime.AddDays(7), gravado.ExpiraEm);
    }

    [FactBanco]
    public async Task Login_EmailComMaiusculasEEspacos_Funciona()
    {
        var (_, email) = await CriarUsuarioAsync();

        var resultado = await LoginAsync("  " + email.ToUpperInvariant() + " ");

        Assert.True(resultado.Sucesso);
    }

    [FactBanco]
    public async Task Login_ComEmailInexistente_FalhaComMotivoGenerico()
    {
        var resultado = await LoginAsync(DadosDeTeste.NovoEmail());

        Assert.False(resultado.Sucesso);
        Assert.Equal(FalhaAutenticacao.CredenciaisInvalidas, resultado.Falha);
    }

    [FactBanco]
    public async Task Login_ComSenhaErrada_FalhaComOMesmoMotivoDoEmailInexistente()
    {
        var (_, email) = await CriarUsuarioAsync();

        var resultado = await LoginAsync(email, "SenhaErrada#999");

        Assert.False(resultado.Sucesso);
        Assert.Equal(FalhaAutenticacao.CredenciaisInvalidas, resultado.Falha);
    }

    [FactBanco]
    public async Task Login_AposCincoSenhasErradas_BloqueiaMesmoComASenhaCorreta()
    {
        var (_, email) = await CriarUsuarioAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.False((await LoginAsync(email, "SenhaErrada#999")).Sucesso);
        }

        var comSenhaCorreta = await LoginAsync(email);

        Assert.False(comSenhaCorreta.Sucesso);
        Assert.Equal(FalhaAutenticacao.CredenciaisInvalidas, comSenhaCorreta.Falha);
    }

    [FactBanco]
    public async Task Login_BemSucedido_ZeraOContadorDeFalhas()
    {
        var (_, email) = await CriarUsuarioAsync();

        for (var i = 0; i < 4; i++) await LoginAsync(email, "SenhaErrada#999");
        Assert.True((await LoginAsync(email)).Sucesso);
        for (var i = 0; i < 4; i++) await LoginAsync(email, "SenhaErrada#999");

        // Sem o reset, as 8 falhas teriam bloqueado a conta.
        Assert.True((await LoginAsync(email)).Sucesso);
    }

    [FactBanco]
    public async Task Login_UsuarioInativo_Falha()
    {
        var (_, email) = await CriarUsuarioAsync(ativo: false);

        Assert.False((await LoginAsync(email)).Sucesso);
    }

    // ---------- renovação (refresh) ----------

    [FactBanco]
    public async Task Renovar_TrocaOTokenEMarcaOAnteriorComoUsado()
    {
        var (id, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;

        var renovado = await RenovarAsync(login.RefreshToken);

        Assert.True(renovado.Sucesso);
        Assert.NotEqual(login.RefreshToken, renovado.Tokens!.RefreshToken);
        Assert.NotEqual(login.AccessToken, renovado.Tokens.AccessToken);

        var tokens = await TokensDoUsuarioAsync(id);
        Assert.Equal(2, tokens.Count);
        var antigo = tokens.Single(t => t.TokenHash == EmissorDeTokens.CalcularHash(login.RefreshToken));
        var novo = tokens.Single(t => t.TokenHash == EmissorDeTokens.CalcularHash(renovado.Tokens.RefreshToken));
        Assert.NotNull(antigo.UsadoEm);
        Assert.Equal(novo.Id, antigo.SubstituidoPorId);
        Assert.Null(novo.UsadoEm);
        Assert.Null(novo.RevogadoEm);
    }

    [FactBanco]
    public async Task Renovar_ComTokenJaUsado_FalhaERevogaTodasAsSessoesDoUsuario()
    {
        var (id, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;
        var renovado = (await RenovarAsync(login.RefreshToken)).Tokens!;

        _relogio.Avancar(TimeSpan.FromSeconds(11)); // fora da janela de tolerância: não é pedido simultâneo
        var reuso = await RenovarAsync(login.RefreshToken); // alguém apresentou de novo o token antigo

        Assert.False(reuso.Sucesso);
        Assert.Equal(FalhaAutenticacao.TokenInvalido, reuso.Falha);
        Assert.False((await RenovarAsync(renovado.RefreshToken)).Sucesso); // o token novo também caiu
        Assert.All(await TokensDoUsuarioAsync(id), t => Assert.NotNull(t.RevogadoEm));
    }

    [FactBanco]
    public async Task Renovar_ComTokenDeOutroUsuario_NaoAfetaOsDemais()
    {
        var (idA, emailA) = await CriarUsuarioAsync();
        var (idB, emailB) = await CriarUsuarioAsync();
        var tokensA = (await LoginAsync(emailA)).Tokens!;
        await LoginAsync(emailB);

        await RenovarAsync(tokensA.RefreshToken);
        _relogio.Avancar(TimeSpan.FromSeconds(11));
        await RenovarAsync(tokensA.RefreshToken); // reúso do A, fora da tolerância

        Assert.All(await TokensDoUsuarioAsync(idA), t => Assert.NotNull(t.RevogadoEm));
        Assert.All(await TokensDoUsuarioAsync(idB), t => Assert.Null(t.RevogadoEm));
    }

    [FactBanco]
    public async Task Renovar_ComTokenExpirado_Falha()
    {
        var (_, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;

        _relogio.Avancar(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));

        Assert.False((await RenovarAsync(login.RefreshToken)).Sucesso);
    }

    [FactBanco]
    public async Task Renovar_ComTokenDesconhecido_Falha()
    {
        var resultado = await RenovarAsync("token-que-nunca-existiu");

        Assert.False(resultado.Sucesso);
        Assert.Equal(FalhaAutenticacao.TokenInvalido, resultado.Falha);
    }

    [FactBanco]
    public async Task Renovar_UsuarioDesativadoDepoisDoLogin_Falha()
    {
        var (id, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;

        await using (var escopo = _provedor.CreateAsyncScope())
        {
            var usuarios = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            var usuario = (await usuarios.FindByIdAsync(id.ToString()))!;
            usuario.Ativo = false;
            await usuarios.UpdateAsync(usuario);
        }

        Assert.False((await RenovarAsync(login.RefreshToken)).Sucesso);
    }

    [FactBanco]
    public async Task Renovar_ComRequisicoesSimultaneas_SoUmaTemSucesso_ESemDerrubarASessao()
    {
        var (id, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;

        var resultados = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Task.Run(() => RenovarAsync(login.RefreshToken))));

        var vencedores = resultados.Where(r => r.Sucesso).ToList();
        Assert.Single(vencedores);
        Assert.All(resultados.Where(r => !r.Sucesso), r => Assert.Equal(FalhaAutenticacao.TokenInvalido, r.Falha));

        // P1-2: as perdedoras NÃO podem derrubar o token novo da vencedora.
        Assert.All(await TokensDoUsuarioAsync(id), x => Assert.Null(x.RevogadoEm));
        Assert.True((await RenovarAsync(vencedores[0].Tokens!.RefreshToken)).Sucesso);
    }

    [FactBanco]
    public async Task Renovar_ReapresentadoDentroDaTolerancia_FalhaMasMantemAsSessoes()
    {
        var (id, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;
        var renovado = (await RenovarAsync(login.RefreshToken)).Tokens!;

        var reapresentado = await RenovarAsync(login.RefreshToken); // dentro dos 10 s: pedido simultâneo legítimo

        Assert.False(reapresentado.Sucesso);
        Assert.All(await TokensDoUsuarioAsync(id), x => Assert.Null(x.RevogadoEm));
        Assert.True((await RenovarAsync(renovado.RefreshToken)).Sucesso); // a sessão da vencedora segue viva
    }

    [FactBanco]
    public async Task Renovar_ComToleranciaZero_QualquerReusoRevogaTudo()
    {
        await using var provedor = ConstruirProvedor(new Dictionary<string, string?> { ["Jwt:RefreshToleranciaSegundos"] = "0" });
        var (id, email) = await CriarUsuarioAsync();

        async Task<ResultadoAutenticacao> Renovar(string refresh)
        {
            await using var escopo = provedor.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IAutenticacaoService>().RenovarAsync(refresh);
        }

        var login = (await LoginAsync(email)).Tokens!;
        var renovado = (await Renovar(login.RefreshToken)).Tokens!;

        Assert.False((await Renovar(login.RefreshToken)).Sucesso); // reúso imediato

        Assert.False((await Renovar(renovado.RefreshToken)).Sucesso); // derrubou o token novo também
        Assert.All(await TokensDoUsuarioAsync(id), x => Assert.NotNull(x.RevogadoEm));
    }

    [FactBanco]
    public async Task Renovar_QuandoAGravacaoDoNovoTokenFalha_NaoConsomeOAntigo()
    {
        var (id, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;

        _falha.Armado = true;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => RenovarAsync(login.RefreshToken));
        }
        finally
        {
            _falha.Armado = false;
        }

        // P1-1: consumo + emissão são atômicos — o token antigo continua intacto...
        var original = Assert.Single(await TokensDoUsuarioAsync(id));
        Assert.Null(original.UsadoEm);
        Assert.Null(original.RevogadoEm);

        // ...e o cliente pode tentar de novo com o mesmo token, sem ser tratado como ladrão.
        Assert.True((await RenovarAsync(login.RefreshToken)).Sucesso);
        Assert.All((await TokensDoUsuarioAsync(id)).Where(x => x.UsadoEm is null), x => Assert.Null(x.RevogadoEm));
    }

    // ---------- tempo de resposta uniforme (P2-1) ----------

    [FactBanco]
    public async Task Login_FalhasDeTodosOsTipos_GastamAMesmaVerificacaoDeHash()
    {
        var (_, existente) = await CriarUsuarioAsync();
        var (_, inativo) = await CriarUsuarioAsync(ativo: false);
        var (_, bloqueado) = await CriarUsuarioAsync();
        for (var i = 0; i < 5; i++) await LoginAsync(bloqueado, "SenhaErrada#999");

        async Task<int> VerificacoesDe(string email, string senha)
        {
            var antes = _hasher.Verificacoes;
            Assert.False((await LoginAsync(email, senha)).Sucesso);
            return _hasher.Verificacoes - antes;
        }

        Assert.Equal(1, await VerificacoesDe(DadosDeTeste.NovoEmail(), DadosDeTeste.SenhaValida)); // inexistente
        Assert.Equal(1, await VerificacoesDe(existente, "SenhaErrada#999"));                         // senha errada
        Assert.Equal(1, await VerificacoesDe(inativo, DadosDeTeste.SenhaValida));                    // inativo
        Assert.Equal(1, await VerificacoesDe(bloqueado, DadosDeTeste.SenhaValida));                  // bloqueado
    }

    // ---------- encerrar sessão ----------

    [FactBanco]
    public async Task EncerrarSessao_RevogaOTokenEImpedeRenovacao_EEhIdempotente()
    {
        var (id, email) = await CriarUsuarioAsync();
        var login = (await LoginAsync(email)).Tokens!;

        await ComServicoAsync(async s => { await s.EncerrarSessaoAsync(login.RefreshToken); return 0; });
        await ComServicoAsync(async s => { await s.EncerrarSessaoAsync(login.RefreshToken); return 0; });
        await ComServicoAsync(async s => { await s.EncerrarSessaoAsync("token-desconhecido"); return 0; });

        Assert.NotNull(Assert.Single(await TokensDoUsuarioAsync(id)).RevogadoEm);
        Assert.False((await RenovarAsync(login.RefreshToken)).Sucesso);
    }

    [FactBanco]
    public async Task RevogarTodas_DerrubaTodasAsSessoesDoUsuario()
    {
        var (id, email) = await CriarUsuarioAsync();
        await LoginAsync(email);
        await LoginAsync(email);
        await LoginAsync(email);

        await ComServicoAsync(async s => { await s.RevogarTodasAsync(id); return 0; });

        var tokens = await TokensDoUsuarioAsync(id);
        Assert.Equal(3, tokens.Count);
        Assert.All(tokens, t => Assert.NotNull(t.RevogadoEm));
    }
}
