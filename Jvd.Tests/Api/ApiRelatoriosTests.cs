using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Api;

/// <summary>Permissões do relatório decididas ANTES de qualquer acesso a dados: sem banco.</summary>
public class ApiRelatoriosSemBancoTests(JvdApiFactory fabrica) : IClassFixture<JvdApiFactory>
{
    private HttpClient ClienteCom(PerfilUsuario? perfil, bool senhaProvisoria = false)
    {
        var cliente = fabrica.CreateClient();
        if (perfil is { } p)
        {
            var emissor = fabrica.Services.GetRequiredService<EmissorDeTokens>();
            var (token, _) = emissor.GerarAccessToken(new Usuario { Id = Guid.NewGuid(), Nome = "Teste", Perfil = p, DeveTrocarSenha = senhaProvisoria });
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return cliente;
    }

    public static IEnumerable<object[]> Rotas()
    {
        var id = Guid.NewGuid();
        return
        [
            ["GET", "/api/relatorios"],
            ["GET", $"/api/relatorios/{id}"],
            ["POST", "/api/relatorios"],
            ["PUT", $"/api/relatorios/{id}"],
            ["DELETE", $"/api/relatorios/{id}"],
            ["POST", $"/api/relatorios/{id}/emitir"]
        ];
    }

    private static HttpRequestMessage Pedido(string metodo, string rota) =>
        new(new HttpMethod(metodo), rota) { Content = metodo is "GET" or "DELETE" ? null : JsonContent.Create(new { }) };

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task SemToken_Retorna401(string metodo, string rota) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClienteCom(null).SendAsync(Pedido(metodo, rota))).StatusCode);

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task ComSenhaProvisoria_Retorna403_MesmoSendoGestor(string metodo, string rota) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await ClienteCom(PerfilUsuario.Gestor, senhaProvisoria: true).SendAsync(Pedido(metodo, rota))).StatusCode);

    [Fact]
    public async Task TipoEStatusInvalidosNaListagem_Retornam400_SemTocarNoBanco()
    {
        var tecnico = ClienteCom(PerfilUsuario.Tecnico);

        Assert.Equal(HttpStatusCode.BadRequest, (await tecnico.GetAsync("/api/relatorios?status=Inventado")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tecnico.GetAsync("/api/relatorios?tipo=2")).StatusCode);   // número não vale, só o nome
    }
}

/// <summary>Relatório pela API HTTP real, com SQL Server. Dados SINTÉTICOS. Só com JVD_TESTES_BANCO=1.</summary>
public class ApiRelatoriosComBancoTests : IClassFixture<BancoDeTeste>, IDisposable
{
    private readonly JvdApiFactoryPersonalizada _fabrica;

    public ApiRelatoriosComBancoTests(BancoDeTeste banco) => _fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);

    public void Dispose() => _fabrica.Dispose();

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(HttpClient Http, Guid Id)> LogarAsync(PerfilUsuario perfil)
    {
        var (id, email) = await ServicosDeTeste.CriarUsuarioAsync(_fabrica.Services, perfil);
        var http = _fabrica.CreateClient();
        var login = await http.PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await LerAsync(login)).GetProperty("accessToken").GetString());
        return (http, id);
    }

    private async Task<(Guid ClienteId, Guid EquipamentoId)> NovoParAsync(Guid gestorId)
    {
        var cliente = await CadastrosDeTeste.NovoClienteAsync(_fabrica.Services, gestorId);
        var equipamento = await CadastrosDeTeste.NovoEquipamentoAsync(_fabrica.Services, gestorId, cliente.Id);
        return (cliente.Id, equipamento.Id);
    }

    private static object Corpo((Guid ClienteId, Guid EquipamentoId) par, string servico = "Troca de peça", string? versao = null) => new
    {
        clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "Corretiva", dataServico = "2026-10-30",
        servicoExecutado = servico, observacoes = "Obs", verificacaoAnteriorData = "2026-01-02", verificacaoAnteriorCertificado = "C-1",
        numeroSeloReparador = "S-1", garantiaDias = 30, total = 100.00m, desconto = 10.00m, versao
    };

    [FactBanco]
    public async Task Fluxo_TecnicoCriaRascunho_Edita_Emite_EOutroTecnicoNaoVe()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var (tecnicoA, _) = await LogarAsync(PerfilUsuario.Tecnico);
        var (tecnicoB, _) = await LogarAsync(PerfilUsuario.Tecnico);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        var par = await NovoParAsync(gestorId);

        var criado = await tecnicoA.PostAsJsonAsync("/api/relatorios", Corpo(par));
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        Assert.NotNull(criado.Headers.Location);
        var rascunho = await LerAsync(criado);
        Assert.Equal("Rascunho", rascunho.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rascunho.GetProperty("numero").ValueKind);
        Assert.Equal(90m, rascunho.GetProperty("totalGeral").GetDecimal());
        Assert.Equal("30 dias (trinta)", rascunho.GetProperty("garantiaExtenso").GetString());
        var id = rascunho.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await tecnicoB.GetAsync($"/api/relatorios/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gestor.GetAsync($"/api/relatorios/{id}")).StatusCode);

        var editado = await tecnicoA.PutAsJsonAsync($"/api/relatorios/{id}", Corpo(par, "Serviço corrigido", rascunho.GetProperty("versao").GetString()));
        Assert.Equal(HttpStatusCode.OK, editado.StatusCode);
        var versao = (await LerAsync(editado)).GetProperty("versao").GetString();

        var emitido = await tecnicoA.PostAsJsonAsync($"/api/relatorios/{id}/emitir", new { versao });
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);
        var corpo = await LerAsync(emitido);
        Assert.Equal("Emitido", corpo.GetProperty("status").GetString());
        Assert.Equal(6, corpo.GetProperty("numeroFormatado").GetString()!.Length);
        Assert.Equal("Empresa Sintética Ltda", corpo.GetProperty("emissor").GetProperty("razaoSocial").GetString());
        Assert.True(corpo.GetProperty("cliente").GetProperty("documento").GetString()!.Length >= 11);

        // depois de emitido: edição direta e descarte são recusados (409), com código estável
        var edicaoDireta = await tecnicoA.PutAsJsonAsync($"/api/relatorios/{id}", Corpo(par, "Alteração", corpo.GetProperty("versao").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, edicaoDireta.StatusCode);
        Assert.Equal("relatorio.nao_e_rascunho", (await LerAsync(edicaoDireta)).GetProperty("codigo").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await tecnicoA.DeleteAsync($"/api/relatorios/{id}")).StatusCode);
    }

    [FactBanco]
    public async Task CamposInternos_NoCorpo_SaoIgnorados_NumeroStatusETotalGeralSaoDaApi()
    {
        var (tecnico, _) = await LogarAsync(PerfilUsuario.Tecnico);
        var (_, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var par = await NovoParAsync(gestorId);

        var resposta = await tecnico.PostAsJsonAsync("/api/relatorios", new
        {
            clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "Corretiva", dataServico = "2026-10-30",
            servicoExecutado = "X", total = 100m, desconto = 10m, totalGeral = 9999m, numero = 42, status = "Emitido", revisao = 7,
            emitidoPor = Guid.NewGuid(), criadoPor = Guid.NewGuid(), clienteSnapshot = "{}"
        });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var corpo = await LerAsync(resposta);
        Assert.Equal("Rascunho", corpo.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("numero").ValueKind);
        Assert.Equal(90m, corpo.GetProperty("totalGeral").GetDecimal());
        Assert.Equal(0, corpo.GetProperty("revisao").GetInt32());
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("emitidoPor").ValueKind);
    }

    [FactBanco]
    public async Task Emitir_ClienteSemDocumento_Retorna400_ComCodigoEstavel()
    {
        var (tecnico, _) = await LogarAsync(PerfilUsuario.Tecnico);
        var (_, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        var cliente = await CadastrosDeTeste.NovoClienteAsync(_fabrica.Services, gestorId, comDocumento: false);
        var equipamento = await CadastrosDeTeste.NovoEquipamentoAsync(_fabrica.Services, gestorId, cliente.Id);
        var criado = await LerAsync(await tecnico.PostAsJsonAsync("/api/relatorios", Corpo((cliente.Id, equipamento.Id))));

        var resposta = await tecnico.PostAsJsonAsync($"/api/relatorios/{criado.GetProperty("id").GetGuid()}/emitir", new { versao = criado.GetProperty("versao").GetString() });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("relatorio.cliente_sem_documento", (await LerAsync(resposta)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task DadosInvalidos_Retornam400_SemEcoarValores()
    {
        var (tecnico, _) = await LogarAsync(PerfilUsuario.Tecnico);
        var (_, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var par = await NovoParAsync(gestorId);

        Assert.Equal(HttpStatusCode.BadRequest, (await tecnico.PostAsJsonAsync("/api/relatorios",
            new { clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "Inventada", dataServico = "2026-10-30" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tecnico.PostAsJsonAsync("/api/relatorios",
            new { clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "1", dataServico = "2026-10-30" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tecnico.PostAsJsonAsync("/api/relatorios",
            new { clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "Corretiva" })).StatusCode);   // sem data

        var descontoMaior = await tecnico.PostAsJsonAsync("/api/relatorios", new
        {
            clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "Corretiva", dataServico = "2026-10-30",
            total = 10m, desconto = 11m
        });
        Assert.Equal(HttpStatusCode.BadRequest, descontoMaior.StatusCode);
        Assert.Equal("validacao", (await LerAsync(descontoMaior)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task Descartar_Rascunho_Retorna204_EDepoisNaoExiste()
    {
        var (tecnico, _) = await LogarAsync(PerfilUsuario.Tecnico);
        var (_, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var par = await NovoParAsync(gestorId);
        var id = (await LerAsync(await tecnico.PostAsJsonAsync("/api/relatorios", Corpo(par)))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await tecnico.DeleteAsync($"/api/relatorios/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tecnico.GetAsync($"/api/relatorios/{id}")).StatusCode);
    }

    [FactBanco]
    public async Task Listagem_TecnicoSoVeOsSeus_GestorTodos_EFiltrosPorTipo()
    {
        var (tecnicoA, _) = await LogarAsync(PerfilUsuario.Tecnico);
        var (tecnicoB, _) = await LogarAsync(PerfilUsuario.Tecnico);
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var par = await NovoParAsync(gestorId);
        await tecnicoA.PostAsJsonAsync("/api/relatorios", Corpo(par));
        await tecnicoB.PostAsJsonAsync("/api/relatorios", Corpo(par));

        var deA = await LerAsync(await tecnicoA.GetAsync($"/api/relatorios?clienteId={par.ClienteId}"));
        var doGestor = await LerAsync(await gestor.GetAsync($"/api/relatorios?clienteId={par.ClienteId}&tipo=corretiva&status=Rascunho"));

        Assert.Equal(1, deA.GetProperty("total").GetInt32());
        Assert.Equal(2, doGestor.GetProperty("total").GetInt32());
        Assert.Equal("Rascunho", doGestor.GetProperty("itens")[0].GetProperty("status").GetString());
    }

    [FactBanco]
    public async Task Gestor_DefineOutroExecutor_TecnicoNao()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var (tecnico, tecnicoId) = await LogarAsync(PerfilUsuario.Tecnico);
        var (outro, outroId) = await LogarAsync(PerfilUsuario.Tecnico);
        var par = await NovoParAsync(gestorId);

        var comExecutor = await gestor.PostAsJsonAsync("/api/relatorios", new
        {
            clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "Instalacao", dataServico = "2026-10-30", tecnicoId
        });
        Assert.Equal(HttpStatusCode.Created, comExecutor.StatusCode);
        Assert.Equal(tecnicoId, (await LerAsync(comExecutor)).GetProperty("tecnicoId").GetGuid());

        var tecnicoTentando = await tecnico.PostAsJsonAsync("/api/relatorios", new
        {
            clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = "Instalacao", dataServico = "2026-10-30", tecnicoId = outroId
        });
        Assert.Equal(HttpStatusCode.BadRequest, tecnicoTentando.StatusCode);
        _ = outro;
    }
}
