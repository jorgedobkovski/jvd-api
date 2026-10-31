using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jvd.Domain;
using Jvd.Tests.Suporte;

namespace Jvd.Tests.Api;

/// <summary>Gestão de usuários e senha provisória pela API HTTP real, com SQL Server. Só com JVD_TESTES_BANCO=1.</summary>
public class ApiUsuariosComBancoTests : IClassFixture<BancoDeTeste>, IDisposable
{
    private const string Rota = "/api/usuarios";

    private readonly JvdApiFactoryPersonalizada _fabrica;

    public ApiUsuariosComBancoTests(BancoDeTeste banco) => _fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);

    public void Dispose() => _fabrica.Dispose();

    // ---------- apoio ----------

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(HttpClient Cliente, Guid Id, string Email)> LogarComoGestorAsync()
    {
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_fabrica.Services, PerfilUsuario.Gestor);
        var cliente = await LogarAsync(email, DadosDeTeste.SenhaValida);
        return (cliente.Cliente, id, email);
    }

    private async Task<(HttpClient Cliente, JsonElement Tokens)> LogarAsync(string email, string senha)
    {
        var cliente = _fabrica.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new { email, senha });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await LerAsync(login);
        cliente.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (cliente, tokens);
    }

    private static object NovoUsuario(string email, string perfil = "Tecnico", string? cpf = null) => new
    {
        nome = "Fulano de Tal", email, perfil, cpf, senhaProvisoria = ServicosDeTeste.SenhaProvisoria
    };

    // ---------- fluxo completo ----------

    [FactBanco]
    public async Task Fluxo_GestorCriaUsuario_UsuarioTrocaASenhaEPassaAUsarOSistema()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();
        var email = DadosDeTeste.NovoEmail();

        // 1. gestor cadastra o técnico com senha provisória
        var criacao = await gestor.PostAsJsonAsync(Rota, NovoUsuario(email, cpf: "529.982.247-25"));
        Assert.Equal(HttpStatusCode.Created, criacao.StatusCode);
        Assert.NotNull(criacao.Headers.Location);
        var criado = await LerAsync(criacao);
        Assert.True(criado.GetProperty("deveTrocarSenha").GetBoolean());
        Assert.Equal("52998224725", criado.GetProperty("cpf").GetString());
        var corpoBruto = criado.GetRawText();
        Assert.DoesNotContain(ServicosDeTeste.SenhaProvisoria, corpoBruto);          // senha nunca volta na resposta
        Assert.DoesNotContain("passwordHash", corpoBruto, StringComparison.OrdinalIgnoreCase);

        // 2. o técnico entra com a provisória: token "travado", só troca de senha e /me
        var (tecnico, tokens) = await LogarAsync(email, ServicosDeTeste.SenhaProvisoria);
        Assert.True(tokens.GetProperty("usuario").GetProperty("deveTrocarSenha").GetBoolean());
        var me = await tecnico.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True((await LerAsync(me)).GetProperty("deveTrocarSenha").GetBoolean());

        // 3. troca a senha: recebe tokens novos, sem a trava, e as sessões antigas caem
        var troca = await tecnico.PostAsJsonAsync("/api/auth/trocar-senha",
            new { senhaAtual = ServicosDeTeste.SenhaProvisoria, novaSenha = ServicosDeTeste.SenhaNova });
        Assert.Equal(HttpStatusCode.OK, troca.StatusCode);
        var novos = await LerAsync(troca);
        Assert.False(novos.GetProperty("usuario").GetProperty("deveTrocarSenha").GetBoolean());

        var antigoRefresh = await tecnico.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, antigoRefresh.StatusCode);

        tecnico.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", novos.GetProperty("accessToken").GetString());
        var meDepois = await tecnico.GetAsync("/api/auth/me");
        Assert.False((await LerAsync(meDepois)).GetProperty("deveTrocarSenha").GetBoolean());

        // 4. técnico (já liberado) continua sem poder gerir usuários: é só do gestor
        Assert.Equal(HttpStatusCode.Forbidden, (await tecnico.GetAsync(Rota)).StatusCode);
    }

    [FactBanco]
    public async Task GestorComSenhaProvisoria_FicaTravadoAteTrocar()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();
        var email = DadosDeTeste.NovoEmail();
        Assert.Equal(HttpStatusCode.Created, (await gestor.PostAsJsonAsync(Rota, NovoUsuario(email, perfil: "Gestor"))).StatusCode);

        var (novoGestor, _) = await LogarAsync(email, ServicosDeTeste.SenhaProvisoria);

        Assert.Equal(HttpStatusCode.Forbidden, (await novoGestor.GetAsync(Rota)).StatusCode);           // travado
        Assert.Equal(HttpStatusCode.OK, (await novoGestor.PostAsJsonAsync("/api/auth/trocar-senha",
            new { senhaAtual = ServicosDeTeste.SenhaProvisoria, novaSenha = ServicosDeTeste.SenhaNova })).StatusCode);
    }

    [FactBanco]
    public async Task RedefinirSenha_PeloGestor_ObrigaATrocaNoProximoLogin()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_fabrica.Services, PerfilUsuario.Tecnico);

        var resposta = await gestor.PostAsJsonAsync($"{Rota}/{id}/redefinir-senha",
            new { novaSenhaProvisoria = ServicosDeTeste.SenhaProvisoria });
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);

        var (_, tokens) = await LogarAsync(email, ServicosDeTeste.SenhaProvisoria);
        Assert.True(tokens.GetProperty("usuario").GetProperty("deveTrocarSenha").GetBoolean());
        var antigaFalha = await _fabrica.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida });
        Assert.Equal(HttpStatusCode.Unauthorized, antigaFalha.StatusCode);
    }

    // ---------- erros e proteções ----------

    [FactBanco]
    public async Task Criar_EmailRepetido_Retorna409ComCodigo()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();
        var email = DadosDeTeste.NovoEmail();
        Assert.Equal(HttpStatusCode.Created, (await gestor.PostAsJsonAsync(Rota, NovoUsuario(email))).StatusCode);

        var repetido = await gestor.PostAsJsonAsync(Rota, NovoUsuario(email));

        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal("usuario.email_em_uso", (await LerAsync(repetido)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task Criar_SenhaFraca_Retorna400SemEcoarASenha()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();

        var resposta = await gestor.PostAsJsonAsync(Rota, new
        {
            nome = "Fulano", email = DadosDeTeste.NovoEmail(), perfil = "Tecnico", senhaProvisoria = "abc123"
        });
        var corpo = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("usuario.senha_invalida", corpo);
        Assert.DoesNotContain("abc123", corpo);
    }

    [FactBanco]
    public async Task Desativar_ASiMesmo_Retorna409()
    {
        var (gestor, id, _) = await LogarComoGestorAsync();

        var resposta = await gestor.PostAsync($"{Rota}/{id}/desativar", null);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("usuario.nao_pode_agir_em_si", (await LerAsync(resposta)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task Obter_Inexistente_Retorna404()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();

        var resposta = await gestor.GetAsync($"{Rota}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("nao_encontrado", (await LerAsync(resposta)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task Listar_RetornaEnvelopeDePaginaESemDadosDeSenha()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();
        var email = DadosDeTeste.NovoEmail();
        await gestor.PostAsJsonAsync(Rota, NovoUsuario(email));

        var resposta = await gestor.GetAsync($"{Rota}?busca={Uri.EscapeDataString(email)}&tamanho=5");
        var corpo = await LerAsync(resposta);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(1, corpo.GetProperty("total").GetInt32());
        Assert.Equal(1, corpo.GetProperty("pagina").GetInt32());
        Assert.Equal(email, corpo.GetProperty("itens")[0].GetProperty("email").GetString());
        Assert.DoesNotContain("hash", corpo.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [FactBanco]
    public async Task Listar_ComFiltroDePerfilInvalido_Retorna400()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.GetAsync($"{Rota}?perfil=Chefe")).StatusCode);
    }

    [FactBanco]
    public async Task Atualizar_PerfilDeOutroUsuario_FuncionaEDesativarReativarRespondem204()
    {
        var (gestor, _, _) = await LogarComoGestorAsync();
        var (id, _) = await ServicosDeTeste.CriarUsuarioAsync(_fabrica.Services, PerfilUsuario.Tecnico);

        var atualizar = await gestor.PutAsJsonAsync($"{Rota}/{id}", new { nome = "Novo Nome", perfil = "Gestor" });
        Assert.Equal(HttpStatusCode.OK, atualizar.StatusCode);
        Assert.Equal("Gestor", (await LerAsync(atualizar)).GetProperty("perfil").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await gestor.PostAsync($"{Rota}/{id}/desativar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await gestor.PostAsync($"{Rota}/{id}/reativar", null)).StatusCode);
    }

    [FactBanco]
    public async Task TrocarSenha_ComSenhaAtualErrada_Retorna400ComCodigo()
    {
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_fabrica.Services, PerfilUsuario.Tecnico);
        var (cliente, _) = await LogarAsync(email, DadosDeTeste.SenhaValida);

        var resposta = await cliente.PostAsJsonAsync("/api/auth/trocar-senha",
            new { senhaAtual = "SenhaErrada#999", novaSenha = ServicosDeTeste.SenhaNova });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode); // 400, não 401: o app não deve deslogar por isso
        Assert.Equal("usuario.senha_atual_incorreta", (await LerAsync(resposta)).GetProperty("codigo").GetString());
        Assert.NotEqual(Guid.Empty, id);
    }
}
