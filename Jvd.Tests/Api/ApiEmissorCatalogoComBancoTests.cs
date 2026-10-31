using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jvd.Domain;
using Jvd.Tests.Suporte;

namespace Jvd.Tests.Api;

/// <summary>
/// Emissor e catálogo pela API HTTP real, com SQL Server. O emissor é um registro único: os testes dele criam o
/// PRÓPRIO banco. Dados SINTÉTICOS. Só com JVD_TESTES_BANCO=1.
/// </summary>
public class ApiEmissorCatalogoComBancoTests : IClassFixture<BancoDeTeste>, IDisposable
{
    private const string Cnpj = "11222333000181";

    private readonly JvdApiFactoryPersonalizada _fabrica;

    public ApiEmissorCatalogoComBancoTests(BancoDeTeste banco) => _fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);

    public void Dispose() => _fabrica.Dispose();

    // ---------- apoio ----------

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<HttpClient> LogarAsync(JvdApiFactoryPersonalizada fabrica, PerfilUsuario perfil)
    {
        var (_, email) = await ServicosDeTeste.CriarUsuarioAsync(fabrica.Services, perfil);
        var cliente = fabrica.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new { email, senha = DadosDeTeste.SenhaValida });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        cliente.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await LerAsync(login)).GetProperty("accessToken").GetString());
        return cliente;
    }

    private Task<HttpClient> LogarAsync(PerfilUsuario perfil) => LogarAsync(_fabrica, perfil);

    private static async Task<(Guid Id, string Versao)> CriarServicoAsync(HttpClient http, string? descricao = null, decimal? preco = null)
    {
        var resposta = await http.PostAsJsonAsync("/api/catalogo-servicos",
            new { descricao = descricao ?? "Serviço sintético " + Guid.NewGuid().ToString("N")[..8], precoSugerido = preco });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var servico = (await LerAsync(resposta)).GetProperty("servico");
        return (servico.GetProperty("id").GetGuid(), servico.GetProperty("versao").GetString()!);
    }

    // ---------- emissor (banco próprio por teste: registro único) ----------

    [FactBanco]
    public async Task Emissor_FluxoCompleto_GestorConfigura_TecnicoConsulta_TecnicoNaoGrava()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        using var fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);
        var gestor = await LogarAsync(fabrica, PerfilUsuario.Gestor);
        var tecnico = await LogarAsync(fabrica, PerfilUsuario.Tecnico);

        Assert.Equal(HttpStatusCode.NotFound, (await tecnico.GetAsync("/api/emissor")).StatusCode);

        var criado = await gestor.PutAsJsonAsync("/api/emissor", new { razaoSocial = "Empresa Sintética Ltda", cnpj = "11.222.333/0001-81" });
        Assert.Equal(HttpStatusCode.OK, criado.StatusCode);
        var versao = (await LerAsync(criado)).GetProperty("versao").GetString();

        var lido = await tecnico.GetAsync("/api/emissor");
        Assert.Equal(HttpStatusCode.OK, lido.StatusCode);
        Assert.Equal(Cnpj, (await LerAsync(lido)).GetProperty("cnpj").GetString());

        Assert.Equal(HttpStatusCode.Forbidden,
            (await tecnico.PutAsJsonAsync("/api/emissor", new { razaoSocial = "Invasor", cnpj = Cnpj, versao })).StatusCode);

        var editado = await gestor.PutAsJsonAsync("/api/emissor", new { razaoSocial = "Nova Razão", cnpj = Cnpj, versao });
        Assert.Equal(HttpStatusCode.OK, editado.StatusCode);
        Assert.Equal("Nova Razão", (await LerAsync(await tecnico.GetAsync("/api/emissor"))).GetProperty("razaoSocial").GetString());

        var velha = await gestor.PutAsJsonAsync("/api/emissor", new { razaoSocial = "Outra", cnpj = Cnpj, versao });
        Assert.Equal(HttpStatusCode.Conflict, velha.StatusCode);
        Assert.Equal("conflito", (await LerAsync(velha)).GetProperty("codigo").GetString());
    }

    [FactBanco]
    public async Task Emissor_DadosInvalidos_Retorna400_SemEcoarOValorDigitado()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        using var fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);
        var gestor = await LogarAsync(fabrica, PerfilUsuario.Gestor);

        var resposta = await gestor.PutAsJsonAsync("/api/emissor",
            new { razaoSocial = "X", cnpj = "12345678000199", responsavelTecnicoCpf = "11111111111" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("12345678000199", texto);
        Assert.DoesNotContain("11111111111", texto);
    }

    [FactBanco]
    public async Task Emissor_SemRazaoSocialOuCnpj_Retorna400()
    {
        await using var banco = await BancoDeTeste.CriarAsync();
        using var fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);
        var gestor = await LogarAsync(fabrica, PerfilUsuario.Gestor);

        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PutAsJsonAsync("/api/emissor", new { cnpj = Cnpj })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PutAsJsonAsync("/api/emissor", new { razaoSocial = "Sem CNPJ" })).StatusCode);
    }

    // ---------- catálogo ----------

    [FactBanco]
    public async Task Catalogo_GestorCriaEEdita_TecnicoSoLe()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);

        var criado = await gestor.PostAsJsonAsync("/api/catalogo-servicos", new { descricao = "Troca de teclado", codigoOrigem = "TT-1", precoSugerido = 180.50m });

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        Assert.NotNull(criado.Headers.Location);
        var corpo = await LerAsync(criado);
        var servico = corpo.GetProperty("servico");
        Assert.Equal(180.50m, servico.GetProperty("precoSugerido").GetDecimal());
        Assert.True(servico.GetProperty("ativo").GetBoolean());
        var id = servico.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await tecnico.GetAsync($"/api/catalogo-servicos/{id}")).StatusCode);

        var tecnicoEscreve = await tecnico.PutAsJsonAsync($"/api/catalogo-servicos/{id}",
            new { descricao = "Invasor", versao = servico.GetProperty("versao").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, tecnicoEscreve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tecnico.PostAsJsonAsync("/api/catalogo-servicos", new { descricao = "Invasor" })).StatusCode);

        var editado = await gestor.PutAsJsonAsync($"/api/catalogo-servicos/{id}",
            new { descricao = "Troca de teclado (nova)", precoSugerido = 200m, versao = servico.GetProperty("versao").GetString() });
        Assert.Equal(HttpStatusCode.OK, editado.StatusCode);
        Assert.Equal("Troca de teclado (nova)", (await LerAsync(editado)).GetProperty("servico").GetProperty("descricao").GetString());
    }

    [FactBanco]
    public async Task Catalogo_VersaoVelha_Retorna409_ESemVersao_Retorna400()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var (id, versao) = await CriarServicoAsync(gestor);
        Assert.Equal(HttpStatusCode.OK, (await gestor.PutAsJsonAsync($"/api/catalogo-servicos/{id}", new { descricao = "Primeira", versao })).StatusCode);

        var velha = await gestor.PutAsJsonAsync($"/api/catalogo-servicos/{id}", new { descricao = "Segunda", versao });
        Assert.Equal(HttpStatusCode.Conflict, velha.StatusCode);
        Assert.Equal("conflito", (await LerAsync(velha)).GetProperty("codigo").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PutAsJsonAsync($"/api/catalogo-servicos/{id}", new { descricao = "Sem versão" })).StatusCode);
    }

    [FactBanco]
    public async Task Catalogo_PrecoInvalido_Retorna400()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);

        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PostAsJsonAsync("/api/catalogo-servicos", new { descricao = "Preço negativo", precoSugerido = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.PostAsJsonAsync("/api/catalogo-servicos", new { descricao = "Três casas", precoSugerido = 1.005m })).StatusCode);
    }

    [FactBanco]
    public async Task Catalogo_DescricaoRepetida_RetornaAvisoComIds()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var descricao = "Intervenção " + Guid.NewGuid().ToString("N")[..8];
        var (primeiro, _) = await CriarServicoAsync(gestor, descricao);

        var resposta = await gestor.PostAsJsonAsync("/api/catalogo-servicos", new { descricao });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var aviso = (await LerAsync(resposta)).GetProperty("avisos")[0];
        Assert.Equal("catalogo.descricao_repetida", aviso.GetProperty("codigo").GetString());
        Assert.Equal(primeiro, aviso.GetProperty("ids")[0].GetGuid());
    }

    [FactBanco]
    public async Task Catalogo_GestorDesativa_TecnicoNaoVeMais_GestorSim_EReativa()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var tecnico = await LogarAsync(PerfilUsuario.Tecnico);
        var marca = "Inativavel" + Guid.NewGuid().ToString("N")[..6];
        var (id, _) = await CriarServicoAsync(gestor, marca);

        Assert.Equal(HttpStatusCode.Forbidden, (await tecnico.PostAsync($"/api/catalogo-servicos/{id}/desativar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await gestor.PostAsync($"/api/catalogo-servicos/{id}/desativar", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await tecnico.GetAsync($"/api/catalogo-servicos/{id}")).StatusCode);
        var listaTecnico = await LerAsync(await tecnico.GetAsync($"/api/catalogo-servicos?busca={marca}&ativo=false"));
        Assert.Equal(0, listaTecnico.GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await gestor.GetAsync($"/api/catalogo-servicos/{id}")).StatusCode);
        var listaGestor = await LerAsync(await gestor.GetAsync($"/api/catalogo-servicos?busca={marca}&ativo=false"));
        Assert.Equal(1, listaGestor.GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await gestor.PostAsync($"/api/catalogo-servicos/{id}/reativar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tecnico.GetAsync($"/api/catalogo-servicos/{id}")).StatusCode);
    }

    [FactBanco]
    public async Task Catalogo_ListagemPaginada_TrazEnvelopeEHonraOTamanho()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);
        var marca = "Pag" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 3; i++) await CriarServicoAsync(gestor, $"{marca} {i}");

        var pagina = await LerAsync(await gestor.GetAsync($"/api/catalogo-servicos?busca={marca}&tamanho=2&pagina=2"));

        Assert.Equal(3, pagina.GetProperty("total").GetInt32());
        Assert.Equal(1, pagina.GetProperty("itens").GetArrayLength());
    }

    [FactBanco]
    public async Task Catalogo_Inexistente_Retorna404()
    {
        var gestor = await LogarAsync(PerfilUsuario.Gestor);

        Assert.Equal(HttpStatusCode.NotFound, (await gestor.GetAsync($"/api/catalogo-servicos/{Guid.NewGuid()}")).StatusCode);
    }
}
