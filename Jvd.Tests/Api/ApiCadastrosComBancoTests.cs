using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jvd.Domain;
using Jvd.Tests.Suporte;

namespace Jvd.Tests.Api;

/// <summary>Clientes e equipamentos pela API HTTP real, com SQL Server. Dados SINTÉTICOS. Só com JVD_TESTES_BANCO=1.</summary>
public class ApiCadastrosComBancoTests : IClassFixture<BancoDeTeste>, IDisposable
{
    private const string CnpjValido = "11222333000181";

    private readonly JvdApiFactoryPersonalizada _fabrica;

    public ApiCadastrosComBancoTests(BancoDeTeste banco) => _fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);

    public void Dispose() => _fabrica.Dispose();

    // ---------- apoio ----------

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpClient> LogarAsync(PerfilUsuario perfil)
    {
        var (_, email) = await ServicosDeTeste.CriarUsuarioAsync(_fabrica.Services, perfil);
        var cliente = _fabrica.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        cliente.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await LerAsync(login)).GetProperty("accessToken").GetString());
        return cliente;
    }

    private static object NovoCliente(string? nome = null, string? documento = null) =>
        new { nomeRazaoSocial = nome ?? "Cliente Sintético " + Guid.NewGuid().ToString("N")[..8], documento };

    private static async Task<(Guid Id, string Versao)> CriarClienteAsync(HttpClient http, object? corpo = null)
    {
        var resposta = await http.PostAsJsonAsync("/api/clientes", corpo ?? NovoCliente());
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var cliente = (await LerAsync(resposta)).GetProperty("cliente");
        return (cliente.GetProperty("id").GetGuid(), cliente.GetProperty("versao").GetString()!);
    }

    // ---------- clientes ----------

    [FactBanco]
    public async Task Tecnico_CriaEEditaCliente_ComLocationEVersao()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);

        var criado = await tecnico.PostAsJsonAsync("/api/clientes", NovoCliente("Mercado Sintético"));

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        Assert.NotNull(criado.Headers.Location);
        var corpo = await LerAsync(criado);
        var cliente = corpo.GetProperty("cliente");
        Assert.True(cliente.GetProperty("cadastroIncompleto").GetBoolean());
        Assert.Equal("Manual", cliente.GetProperty("origem").GetString());
        Assert.Equal(0, corpo.GetProperty("avisos").GetArrayLength());

        var id = cliente.GetProperty("id").GetGuid();
        var editado = await tecnico.PutAsJsonAsync($"/api/clientes/{id}", new
        {
            nomeRazaoSocial = "Mercado Sintético Ltda", documento = CnpjValido, versao = cliente.GetProperty("versao").GetString()
        });
        Assert.Equal(HttpStatusCode.OK, editado.StatusCode);
        Assert.False((await LerAsync(editado)).GetProperty("cliente").GetProperty("cadastroIncompleto").GetBoolean());
    }

    [FactBanco]
    public async Task Cliente_VersaoVelha_Retorna409ComCodigoConflito()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var (id, versao) = await CriarClienteAsync(tecnico);
        Assert.Equal(HttpStatusCode.OK, (await tecnico.PutAsJsonAsync($"/api/clientes/{id}",
            new { nomeRazaoSocial = "Primeira", versao })).StatusCode);

        var resposta = await tecnico.PutAsJsonAsync($"/api/clientes/{id}", new { nomeRazaoSocial = "Segunda", versao });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("conflito", (await LerAsync(resposta)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task Cliente_SemVersaoNoCorpo_Retorna400()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var (id, _) = await CriarClienteAsync(tecnico);

        var resposta = await tecnico.PutAsJsonAsync($"/api/clientes/{id}", new { nomeRazaoSocial = "Sem versao" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [FactBanco]
    public async Task Cliente_DadosInvalidos_Retorna400_SemEcoarOValorDigitado()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);

        var resposta = await tecnico.PostAsJsonAsync("/api/clientes", new { nomeRazaoSocial = "X", documento = "12345678901", email = "segredo-invalido" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("12345678901", texto);
        Assert.DoesNotContain("segredo-invalido", texto);
    }

    [FactBanco]
    public async Task Cliente_DocumentoRepetido_RetornaAvisoComIds()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var documento = DadosDeTeste.NovoCpf();
        var (primeiro, _) = await CriarClienteAsync(tecnico, NovoCliente(documento: documento));

        var resposta = await tecnico.PostAsJsonAsync("/api/clientes", NovoCliente(documento: documento));

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var aviso = (await LerAsync(resposta)).GetProperty("avisos")[0];
        Assert.Equal("cliente.documento_repetido", aviso.GetProperty("codigo").GetString());
        Assert.Equal(primeiro, aviso.GetProperty("ids")[0].GetGuid());
    }

    [FactBanco]
    public async Task Gestor_DesativaCliente_TecnicoNaoOEnxergaMais_GestorSim()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var (id, _) = await CriarClienteAsync(gestor);

        Assert.Equal(HttpStatusCode.NoContent, (await gestor.PostAsync($"/api/clientes/{id}/desativar", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await tecnico.GetAsync($"/api/clientes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gestor.GetAsync($"/api/clientes/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await gestor.PostAsync($"/api/clientes/{id}/reativar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tecnico.GetAsync($"/api/clientes/{id}")).StatusCode);
    }

    [FactBanco]
    public async Task Tecnico_NaoDesativaCliente_Retorna403()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var (id, _) = await CriarClienteAsync(tecnico);

        Assert.Equal(HttpStatusCode.Forbidden, (await tecnico.PostAsync($"/api/clientes/{id}/desativar", null)).StatusCode);
    }

    [FactBanco]
    public async Task Listagem_DeClientes_TrazPaginaEHonraOTamanho()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var marca = "Lista" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 3; i++) await CriarClienteAsync(tecnico, NovoCliente($"{marca} {i}"));

        var resposta = await tecnico.GetAsync($"/api/clientes?busca={marca}&tamanho=2&pagina=2");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var pagina = await LerAsync(resposta);
        Assert.Equal(3, pagina.GetProperty("total").GetInt32());
        Assert.Equal(1, pagina.GetProperty("itens").GetArrayLength());
    }

    [FactBanco]
    public async Task Cliente_Inexistente_Retorna404()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);

        Assert.Equal(HttpStatusCode.NotFound, (await gestor.GetAsync($"/api/clientes/{Guid.NewGuid()}")).StatusCode);
    }

    // ---------- equipamentos ----------

    [FactBanco]
    public async Task Tecnico_CriaEquipamento_EVeNaListagemDoCliente()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var (clienteId, _) = await CriarClienteAsync(tecnico);

        var criado = await tecnico.PostAsJsonAsync("/api/equipamentos", new
        {
            clienteId, descricao = "Balança sintética", numeroSerie = "SER-HTTP-1", numeroInmetro = "INM-HTTP-1"
        });

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var equipamento = (await LerAsync(criado)).GetProperty("equipamento");

        var lista = await LerAsync(await tecnico.GetAsync($"/api/equipamentos?clienteId={clienteId}"));
        Assert.Equal(1, lista.GetProperty("total").GetInt32());
        Assert.Equal(equipamento.GetProperty("id").GetGuid(), lista.GetProperty("itens")[0].GetProperty("id").GetGuid());
    }

    [FactBanco]
    public async Task Equipamento_ClienteInexistente_Retorna400()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);

        var resposta = await tecnico.PostAsJsonAsync("/api/equipamentos", new { clienteId = Guid.NewGuid(), descricao = "Balança" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [FactBanco]
    public async Task Equipamento_TransfereDeCliente_E409ComVersaoVelha()
    {
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var (origem, _) = await CriarClienteAsync(tecnico);
        var (destino, _) = await CriarClienteAsync(tecnico);
        var criado = await LerAsync(await tecnico.PostAsJsonAsync("/api/equipamentos", new { clienteId = origem, descricao = "Balança" }));
        var equipamento = criado.GetProperty("equipamento");
        var id = equipamento.GetProperty("id").GetGuid();
        var versao = equipamento.GetProperty("versao").GetString();

        var transferido = await tecnico.PutAsJsonAsync($"/api/equipamentos/{id}", new { clienteId = destino, descricao = "Balança", versao });
        Assert.Equal(HttpStatusCode.OK, transferido.StatusCode);
        Assert.Equal(destino, (await LerAsync(transferido)).GetProperty("equipamento").GetProperty("clienteId").GetGuid());

        var velha = await tecnico.PutAsJsonAsync($"/api/equipamentos/{id}", new { clienteId = origem, descricao = "Balança", versao });
        Assert.Equal(HttpStatusCode.Conflict, velha.StatusCode);
    }

    [FactBanco]
    public async Task Equipamento_TecnicoNaoDesativa_GestorDesativaETecnicoNaoOVeMais()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var (clienteId, _) = await CriarClienteAsync(gestor);
        var criado = await LerAsync(await gestor.PostAsJsonAsync("/api/equipamentos", new { clienteId, descricao = "Balança" }));
        var id = criado.GetProperty("equipamento").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await tecnico.PostAsync($"/api/equipamentos/{id}/desativar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await gestor.PostAsync($"/api/equipamentos/{id}/desativar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tecnico.GetAsync($"/api/equipamentos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gestor.GetAsync($"/api/equipamentos/{id}")).StatusCode);
    }
}
