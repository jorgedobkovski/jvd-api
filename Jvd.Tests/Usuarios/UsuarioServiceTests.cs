using Jvd.Application.Autenticacao;
using Jvd.Application.Comum;
using Jvd.Application.Usuarios;
using Jvd.Domain;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Usuarios;

/// <summary>Regras de gestão de usuários sobre SQL Server real (banco descartável). Só com JVD_TESTES_BANCO=1.</summary>
public class UsuarioServiceTests : IClassFixture<BancoDeTeste>, IAsyncLifetime
{
    private const string CpfValido = "52998224725";

    private readonly BancoDeTeste _banco;
    private readonly RelogioFalso _relogio = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _sp = null!;

    public UsuarioServiceTests(BancoDeTeste banco) => _banco = banco;

    public Task InitializeAsync()
    {
        _sp = ServicosDeTeste.Construir(_banco.Conexao, _relogio);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _sp.DisposeAsync();

    // ---------- apoio ----------

    private static async Task<T> Servico<T>(IServiceProvider sp, Func<IUsuarioService, Task<T>> acao)
    {
        await using var escopo = sp.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IUsuarioService>());
    }

    private Task<T> Servico<T>(Func<IUsuarioService, Task<T>> acao) => Servico(_sp, acao);

    private static CriarUsuarioComando Cmd(
        Guid ator, string? email = null, string nome = "Fulano de Tal", PerfilUsuario perfil = PerfilUsuario.Tecnico,
        string? cpf = null, string senha = ServicosDeTeste.SenhaProvisoria) =>
        new(nome, email ?? DadosDeTeste.NovoEmail(), perfil, cpf, senha, ator);

    private async Task<List<EventoDeCadastro>> EventosAsync(Guid entidadeId)
    {
        await using var escopo = _sp.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        return await db.EventosDeCadastro.AsNoTracking()
            .Where(e => e.EntidadeId == entidadeId).OrderBy(e => e.OcorridoEm).ThenBy(e => e.Tipo).ToListAsync();
    }

    private async Task<Usuario> CarregarAsync(Guid id, IServiceProvider? sp = null)
    {
        await using var escopo = (sp ?? _sp).CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private async Task<List<RefreshToken>> TokensAsync(Guid usuarioId, IServiceProvider? sp = null)
    {
        await using var escopo = (sp ?? _sp).CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        return await db.RefreshTokens.AsNoTracking().Where(t => t.UsuarioId == usuarioId).ToListAsync();
    }

    private async Task<ResultadoAutenticacao> LoginAsync(string email, string senha)
    {
        await using var escopo = _sp.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IAutenticacaoService>()
            .LoginAsync(new LoginComando(email, senha));
    }

    // ---------- criar ----------

    [FactBanco]
    public async Task Criar_ComDadosValidos_CriaUsuarioComTrocaObrigatoriaEAuditoria()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var email = DadosDeTeste.NovoEmail();

        var resultado = await Servico(s => s.CriarAsync(
            Cmd(ator, email, nome: "  Maria Silva  ", cpf: "529.982.247-25")));

        Assert.True(resultado.Sucesso);
        var criado = resultado.Valor!;
        Assert.Equal("Maria Silva", criado.Nome);                 // aparado
        Assert.Equal(CpfValido, criado.Cpf);                      // só dígitos
        Assert.Equal(PerfilUsuario.Tecnico, criado.Perfil);
        Assert.True(criado.Ativo);
        Assert.True(criado.DeveTrocarSenha);                      // ADR-006

        var gravado = await CarregarAsync(criado.Id);
        Assert.NotEqual(ServicosDeTeste.SenhaProvisoria, gravado.PasswordHash); // só o hash
        Assert.Equal(email, gravado.UserName);                    // UserName == e-mail

        var evento = Assert.Single(await EventosAsync(criado.Id));
        Assert.Equal(TipoEventoCadastro.Criado, evento.Tipo);
        Assert.Equal(ator, evento.AtorId);
        Assert.Equal("nome,email,perfil,cpf", evento.Campos);
        // a trilha guarda NOMES de campos, nunca valores (LGPD)
        Assert.DoesNotContain(email, evento.Campos);
        Assert.DoesNotContain("Maria", evento.Campos);
        Assert.DoesNotContain(CpfValido, evento.Campos);
    }

    [FactBanco]
    public async Task Criar_SemCpf_EhPermitido()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);

        var resultado = await Servico(s => s.CriarAsync(Cmd(ator, cpf: null)));

        Assert.True(resultado.Sucesso);
        Assert.Null(resultado.Valor!.Cpf);
        Assert.Equal("nome,email,perfil", Assert.Single(await EventosAsync(resultado.Valor.Id)).Campos);
    }

    [FactBanco]
    public async Task Criar_ComEmailJaCadastrado_FalhaMesmoComMaiusculas()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var email = DadosDeTeste.NovoEmail();
        Assert.True((await Servico(s => s.CriarAsync(Cmd(ator, email)))).Sucesso);

        var repetido = await Servico(s => s.CriarAsync(Cmd(ator, email.ToUpperInvariant())));

        Assert.False(repetido.Sucesso);
        Assert.Equal(CodigosDeErro.EmailEmUso, repetido.Erros[0].Codigo);
    }

    [FactBanco]
    public async Task Criar_ComSenhaFraca_FalhaSemEcoarASenha()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var email = DadosDeTeste.NovoEmail();

        var resultado = await Servico(s => s.CriarAsync(Cmd(ator, email, senha: "abc123")));

        Assert.False(resultado.Sucesso);
        Assert.All(resultado.Erros, e => Assert.Equal(CodigosDeErro.SenhaInvalida, e.Codigo));
        Assert.All(resultado.Erros, e => Assert.DoesNotContain("abc123", e.Mensagem));
        // não criou nada: o mesmo e-mail continua livre
        Assert.True((await Servico(s => s.CriarAsync(Cmd(ator, email)))).Sucesso);
    }

    [FactBanco]
    public async Task Criar_ComDadosInvalidos_FalhaComValidacaoENaoCria()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);

        var casos = new[]
        {
            Cmd(ator, nome: "   "),
            Cmd(ator, nome: new string('x', 151)),
            Cmd(ator, email: "isto-nao-e-email"),
            Cmd(ator, email: "sem-ponto@dominio"),
            Cmd(ator, cpf: "123.456.789-00"),
            Cmd(ator, perfil: (PerfilUsuario)99),
            Cmd(ator, senha: "   ")
        };

        foreach (var caso in casos)
        {
            var resultado = await Servico(s => s.CriarAsync(caso));
            Assert.False(resultado.Sucesso);
            Assert.Equal(CodigosDeErro.Validacao, resultado.Erros[0].Codigo);
        }

        var pagina = await Servico(s => s.ListarAsync(new FiltroDeUsuarios("sem-ponto", null, null)));
        Assert.Empty(pagina.Itens);
    }

    // ---------- atualizar ----------

    [FactBanco]
    public async Task Atualizar_AlteraNomeECpf_RegistraSoNomesDeCampos()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var (id, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico, nome: "Nome Antigo");

        var resultado = await Servico(s => s.AtualizarAsync(
            new AtualizarUsuarioComando(id, " Nome Novo ", PerfilUsuario.Tecnico, "529.982.247-25", ator)));

        Assert.True(resultado.Sucesso);
        Assert.Equal("Nome Novo", resultado.Valor!.Nome);
        Assert.Equal(CpfValido, resultado.Valor.Cpf);

        var evento = Assert.Single(await EventosAsync(id));
        Assert.Equal(TipoEventoCadastro.Editado, evento.Tipo);
        Assert.Equal("nome,cpf", evento.Campos);
        Assert.DoesNotContain("Nome", evento.Campos);
    }

    [FactBanco]
    public async Task Atualizar_SemMudancas_NaoGeraEvento()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var (id, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico, nome: "Igual");

        var resultado = await Servico(s => s.AtualizarAsync(
            new AtualizarUsuarioComando(id, "Igual", PerfilUsuario.Tecnico, null, ator)));

        Assert.True(resultado.Sucesso);
        Assert.Empty(await EventosAsync(id));
    }

    [FactBanco]
    public async Task Atualizar_MudarOPerfil_RevogaAsSessoesDoUsuario()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico);
        Assert.True((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);

        var resultado = await Servico(s => s.AtualizarAsync(
            new AtualizarUsuarioComando(id, "Usuário de Teste", PerfilUsuario.Gestor, null, ator)));

        Assert.True(resultado.Sucesso);
        Assert.Equal(PerfilUsuario.Gestor, resultado.Valor!.Perfil);
        Assert.All(await TokensAsync(id), t => Assert.NotNull(t.RevogadoEm)); // o perfil vai dentro do token
        Assert.Contains("perfil", Assert.Single(await EventosAsync(id)).Campos!);
    }

    [FactBanco]
    public async Task Atualizar_UsuarioInexistente_RetornaNaoEncontrado()
    {
        var resultado = await Servico(s => s.AtualizarAsync(
            new AtualizarUsuarioComando(Guid.NewGuid(), "X", PerfilUsuario.Tecnico, null, Guid.NewGuid())));

        Assert.Equal(CodigosDeErro.NaoEncontrado, resultado.Erros[0].Codigo);
    }

    [FactBanco]
    public async Task Atualizar_DadosInvalidos_Falha()
    {
        var (id, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp);

        var resultado = await Servico(s => s.AtualizarAsync(
            new AtualizarUsuarioComando(id, "", PerfilUsuario.Tecnico, "111.111.111-11", Guid.NewGuid())));

        Assert.False(resultado.Sucesso);
        Assert.Equal(2, resultado.Erros.Count); // nome e CPF
    }

    // ---------- desativar / reativar ----------

    [FactBanco]
    public async Task Desativar_RevogaSessoesImpedeLoginEEhIdempotente_ReativarRestaura()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico);
        Assert.True((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);

        Assert.True((await Servico(s => s.DesativarAsync(id, ator))).Sucesso);
        Assert.True((await Servico(s => s.DesativarAsync(id, ator))).Sucesso); // idempotente

        Assert.False((await CarregarAsync(id)).Ativo);
        Assert.All(await TokensAsync(id), t => Assert.NotNull(t.RevogadoEm));
        Assert.False((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);
        Assert.Single(await EventosAsync(id), e => e.Tipo == TipoEventoCadastro.Desativado); // 1 só: a repetição não duplica

        Assert.True((await Servico(s => s.ReativarAsync(id, ator))).Sucesso);
        Assert.True((await Servico(s => s.ReativarAsync(id, ator))).Sucesso); // idempotente
        Assert.True((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);
        Assert.Single(await EventosAsync(id), e => e.Tipo == TipoEventoCadastro.Reativado);
    }

    [FactBanco]
    public async Task Desativar_ASiMesmo_EhRecusado()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);

        var resultado = await Servico(s => s.DesativarAsync(ator, ator));

        Assert.Equal(CodigosDeErro.NaoPodeAgirEmSi, resultado.Erros[0].Codigo);
        Assert.True((await CarregarAsync(ator)).Ativo);
    }

    [FactBanco]
    public async Task Desativar_ouReativar_UsuarioInexistente_RetornaNaoEncontrado()
    {
        Assert.Equal(CodigosDeErro.NaoEncontrado, (await Servico(s => s.DesativarAsync(Guid.NewGuid(), Guid.NewGuid()))).Erros[0].Codigo);
        Assert.Equal(CodigosDeErro.NaoEncontrado, (await Servico(s => s.ReativarAsync(Guid.NewGuid(), Guid.NewGuid()))).Erros[0].Codigo);
    }

    // ---------- redefinir senha ----------

    [FactBanco]
    public async Task RedefinirSenha_ExigeTrocaDesbloqueiaERevogaSessoes()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico);
        Assert.True((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);
        for (var i = 0; i < 5; i++) await LoginAsync(email, "SenhaErrada#999"); // esqueceu e se bloqueou
        Assert.False((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);

        var resultado = await Servico(s => s.RedefinirSenhaAsync(id, ServicosDeTeste.SenhaProvisoria, ator));

        Assert.True(resultado.Sucesso);
        Assert.True((await CarregarAsync(id)).DeveTrocarSenha);
        Assert.All(await TokensAsync(id), t => Assert.NotNull(t.RevogadoEm));
        Assert.False((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);          // senha antiga morreu
        var novo = await LoginAsync(email, ServicosDeTeste.SenhaProvisoria);                // e a conta está desbloqueada
        Assert.True(novo.Sucesso);
        Assert.True(novo.Tokens!.Usuario.DeveTrocarSenha);

        var evento = Assert.Single(await EventosAsync(id), e => e.Tipo == TipoEventoCadastro.SenhaRedefinida);
        Assert.Equal(ator, evento.AtorId);
        Assert.Equal("senha", evento.Campos);
    }

    [FactBanco]
    public async Task RedefinirSenha_ComSenhaFraca_FalhaEMantemASenhaAtual()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Tecnico);

        var resultado = await Servico(s => s.RedefinirSenhaAsync(id, "curta", ator));

        Assert.False(resultado.Sucesso);
        Assert.All(resultado.Erros, e => Assert.DoesNotContain("curta", e.Mensagem));
        Assert.True((await LoginAsync(email, DadosDeTeste.SenhaValida)).Sucesso);
        Assert.False((await CarregarAsync(id)).DeveTrocarSenha);
    }

    [FactBanco]
    public async Task RedefinirSenha_DaPropriaConta_EhRecusada()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);

        var resultado = await Servico(s => s.RedefinirSenhaAsync(ator, ServicosDeTeste.SenhaProvisoria, ator));

        Assert.Equal(CodigosDeErro.NaoPodeAgirEmSi, resultado.Erros[0].Codigo);
    }

    [FactBanco]
    public async Task RedefinirSenha_Inexistente_OuVazia_Falha()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var (id, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp);

        Assert.Equal(CodigosDeErro.NaoEncontrado,
            (await Servico(s => s.RedefinirSenhaAsync(Guid.NewGuid(), ServicosDeTeste.SenhaProvisoria, ator))).Erros[0].Codigo);
        Assert.Equal(CodigosDeErro.Validacao,
            (await Servico(s => s.RedefinirSenhaAsync(id, "  ", ator))).Erros[0].Codigo);
    }

    // ---------- listar / obter ----------

    [FactBanco]
    public async Task Listar_AplicaFiltrosOrdenacaoEPaginacao()
    {
        var (ator, _) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor);
        var marca = "Marca" + Guid.NewGuid().ToString("N")[..10];
        var ids = new List<Guid>();
        foreach (var (nome, perfil) in new[]
                 {
                     ($"{marca} Dora", PerfilUsuario.Tecnico), ($"{marca} Ana", PerfilUsuario.Tecnico),
                     ($"{marca} Caio", PerfilUsuario.Gestor), ($"{marca} Bia", PerfilUsuario.Tecnico),
                     ($"{marca} Edu", PerfilUsuario.Tecnico)
                 })
        {
            ids.Add((await ServicosDeTeste.CriarUsuarioAsync(_sp, perfil, nome: nome)).Id);
        }
        await Servico(s => s.DesativarAsync(ids[4], ator)); // Edu inativo

        var todos = await Servico(s => s.ListarAsync(new FiltroDeUsuarios(marca, null, null)));
        Assert.Equal(5, todos.Total);
        Assert.Equal(
            [$"{marca} Ana", $"{marca} Bia", $"{marca} Caio", $"{marca} Dora", $"{marca} Edu"],
            todos.Itens.Select(i => i.Nome).ToArray());                                  // ordenado por nome

        Assert.Equal(1, (await Servico(s => s.ListarAsync(new FiltroDeUsuarios(marca, PerfilUsuario.Gestor, null)))).Total);
        Assert.Equal(4, (await Servico(s => s.ListarAsync(new FiltroDeUsuarios(marca, null, true)))).Total);
        Assert.Equal(1, (await Servico(s => s.ListarAsync(new FiltroDeUsuarios(marca, null, false)))).Total);

        var pagina2 = await Servico(s => s.ListarAsync(new FiltroDeUsuarios(marca, null, null, Pagina: 2, Tamanho: 2)));
        Assert.Equal(5, pagina2.Total);
        Assert.Equal([$"{marca} Caio", $"{marca} Dora"], pagina2.Itens.Select(i => i.Nome).ToArray());
        Assert.Equal(2, pagina2.Pagina);
    }

    [FactBanco]
    public async Task Listar_LimitaOTamanhoDaPaginaENormalizaPagina()
    {
        var resultado = await Servico(s => s.ListarAsync(new FiltroDeUsuarios(null, null, null, Pagina: -3, Tamanho: 100000)));

        Assert.Equal(1, resultado.Pagina);
        Assert.Equal(100, resultado.Tamanho);
    }

    [FactBanco]
    public async Task Listar_BuscaPorEmail_TambemFunciona()
    {
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_sp);

        var resultado = await Servico(s => s.ListarAsync(new FiltroDeUsuarios(email, null, null)));

        Assert.Equal(id, Assert.Single(resultado.Itens).Id);
    }

    [FactBanco]
    public async Task Obter_Existente_RetornaResumoSemSenha_EInexistenteNaoEncontrado()
    {
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_sp, PerfilUsuario.Gestor, nome: "Obter Teste");

        var achado = await Servico(s => s.ObterAsync(id));
        var ausente = await Servico(s => s.ObterAsync(Guid.NewGuid()));

        Assert.True(achado.Sucesso);
        Assert.Equal(email, achado.Valor!.Email);
        Assert.Equal(PerfilUsuario.Gestor, achado.Valor.Perfil);
        Assert.Equal(CodigosDeErro.NaoEncontrado, ausente.Erros[0].Codigo);
    }

    // ---------- último gestor (cada teste com banco próprio: precisa de contagem exata) ----------

    [FactBanco]
    public async Task UltimoGestor_NaoPodeSerDesativadoNemRebaixado()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        await using var sp = ServicosDeTeste.Construir(banco.Conexao, _relogio);
        var (gestor, _) = await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor);
        var outroAtor = Guid.NewGuid();

        var desativar = await Servico(sp, s => s.DesativarAsync(gestor, outroAtor));
        var rebaixar = await Servico(sp, s => s.AtualizarAsync(
            new AtualizarUsuarioComando(gestor, "Usuário de Teste", PerfilUsuario.Tecnico, null, outroAtor)));

        Assert.Equal(CodigosDeErro.UltimoGestor, desativar.Erros[0].Codigo);
        Assert.Equal(CodigosDeErro.UltimoGestor, rebaixar.Erros[0].Codigo);
        var depois = await CarregarAsync(gestor, sp);
        Assert.True(depois.Ativo);
        Assert.Equal(PerfilUsuario.Gestor, depois.Perfil);
    }

    [FactBanco]
    public async Task Gestor_NaoPodeRebaixarASiMesmo_MesmoHavendoOutroGestor()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        await using var sp = ServicosDeTeste.Construir(banco.Conexao, _relogio);
        var (a, _) = await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor);
        await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor);

        var resultado = await Servico(sp, s => s.AtualizarAsync(
            new AtualizarUsuarioComando(a, "Usuário de Teste", PerfilUsuario.Tecnico, null, a)));

        Assert.Equal(CodigosDeErro.NaoPodeAgirEmSi, resultado.Erros[0].Codigo);
    }

    [FactBanco]
    public async Task Gestor_PodeDesativarOutroGestor_QuandoHaMaisDeUmAtivo()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        await using var sp = ServicosDeTeste.Construir(banco.Conexao, _relogio);
        var (a, _) = await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor);
        var (b, _) = await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor);

        Assert.True((await Servico(sp, s => s.DesativarAsync(b, a))).Sucesso);
        // e agora A é o último: ninguém (nem um terceiro) consegue desativá-lo
        Assert.Equal(CodigosDeErro.UltimoGestor, (await Servico(sp, s => s.DesativarAsync(a, Guid.NewGuid()))).Erros[0].Codigo);
    }

    [FactBanco]
    public async Task DoisGestoresSeDesativandoAoMesmoTempo_NuncaDeixamOSistemaSemGestor()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        await using var sp = ServicosDeTeste.Construir(banco.Conexao, _relogio);
        var (a, _) = await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor);
        var (b, _) = await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor);

        // A desativa B e, ao mesmo tempo, B desativa A: se os dois passassem, o sistema ficaria sem gestor.
        var resultados = await Task.WhenAll(
            Task.Run(() => Servico(sp, s => s.DesativarAsync(b, a))),
            Task.Run(() => Servico(sp, s => s.DesativarAsync(a, b))));

        await using var escopo = sp.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        var ativos = await db.Users.CountAsync(u => u.Perfil == PerfilUsuario.Gestor && u.Ativo);

        Assert.True(ativos >= 1, "o sistema ficou sem gestor ativo");
        Assert.True(resultados.Count(r => r.Sucesso) <= 1);
        Assert.All(resultados.Where(r => !r.Sucesso), r =>
            Assert.Contains(r.Erros[0].Codigo, new[] { CodigosDeErro.UltimoGestor, CodigosDeErro.Conflito }));
    }
}
