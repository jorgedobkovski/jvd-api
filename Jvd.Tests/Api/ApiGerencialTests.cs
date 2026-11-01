using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Tests.Suporte;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Api;

/// <summary>Permissões e validações da visão gerencial (Fase 8B) decididas ANTES de qualquer acesso a dados: sem banco.</summary>
public class ApiGerencialSemBancoTests(JvdApiFactory fabrica) : IClassFixture<JvdApiFactory>
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

    public static IEnumerable<object[]> Rotas() =>
    [
        ["/api/gerencial/indicadores"],
        ["/api/gerencial/exportar-relatorios"]
    ];

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task SemToken_Retorna401(string rota) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClienteCom(null).GetAsync(rota)).StatusCode);

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task Tecnico_NaoVeValoresNemExporta_Retorna403(string rota) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await ClienteCom(PerfilUsuario.Tecnico).GetAsync(rota)).StatusCode);

    [Theory]
    [MemberData(nameof(Rotas))]
    public async Task GestorComSenhaProvisoria_Retorna403(string rota) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await ClienteCom(PerfilUsuario.Gestor, senhaProvisoria: true).GetAsync(rota)).StatusCode);

    [Fact]
    public async Task Exportar_TipoDeServicoInvalido_Retorna400SemTocarNoBanco()
    {
        var resposta = await ClienteCom(PerfilUsuario.Gestor).GetAsync("/api/gerencial/exportar-relatorios?tipo=Inventado");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("validacao", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData("/api/gerencial/indicadores?de=ontem")]
    [InlineData("/api/gerencial/indicadores?ate=2026-13-45")]
    [InlineData("/api/gerencial/exportar-relatorios?de=2026-02-30")]
    [InlineData("/api/gerencial/exportar-relatorios?clienteId=nao-e-guid")]
    public async Task ParametrosMalFormados_Retornam400(string rota) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await ClienteCom(PerfilUsuario.Gestor).GetAsync(rota)).StatusCode);
}

/// <summary>
/// Indicadores e exportação pela API HTTP real, com SQL Server. Dados SINTÉTICOS; cada teste usa um período próprio
/// (anos distintos) para não se misturar com os demais. Só com JVD_TESTES_BANCO=1.
/// </summary>
public class ApiGerencialComBancoTests : IClassFixture<BancoDeTeste>, IDisposable
{
    private readonly JvdApiFactoryPersonalizada _fabrica;

    public ApiGerencialComBancoTests(BancoDeTeste banco) => _fabrica = new JvdApiFactoryPersonalizada(banco.Conexao);

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

    /// <summary>Cria e emite um relatório pelo HTTP, como o executor <paramref name="executor"/>. Devolve o JSON emitido.</summary>
    private static async Task<JsonElement> EmitirAsync(
        HttpClient executor, (Guid ClienteId, Guid EquipamentoId) par, string data, decimal total, decimal desconto, string tipo = "Corretiva")
    {
        var criado = await executor.PostAsJsonAsync("/api/relatorios", new
        {
            clienteId = par.ClienteId, equipamentoId = par.EquipamentoId, tipoServico = tipo, dataServico = data,
            servicoExecutado = "Serviço de teste", garantiaDias = 30, total, desconto
        });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var rascunho = await LerAsync(criado);
        var emitido = await executor.PostAsJsonAsync($"/api/relatorios/{rascunho.GetProperty("id").GetGuid()}/emitir", new { versao = rascunho.GetProperty("versao").GetString() });
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);
        return await LerAsync(emitido);
    }

    private static string Periodo(string ano) => $"de={ano}-01-01&ate={ano}-12-31";

    [FactBanco]
    public async Task Indicadores_SomamSoEmitidos_PorPeriodo_ComRankings()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var (tecnico, tecnicoId) = await LogarAsync(PerfilUsuario.Tecnico);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        var par1 = await NovoParAsync(gestorId);
        var par2 = await NovoParAsync(gestorId);

        await EmitirAsync(gestor, par1, "2031-02-10", 100m, 10m);                       // gestor, cliente 1, corretiva: 90
        await EmitirAsync(tecnico, par2, "2031-05-20", 200m, 0m, "Preventiva");           // técnico, cliente 2, preventiva: 200
        await EmitirAsync(tecnico, par1, "2031-07-01", 50m, 0m);                          // técnico, cliente 1, corretiva: 50
        await EmitirAsync(gestor, par1, "2032-01-01", 999m, 0m);                          // fora do período

        // cancelado e rascunho NUNCA entram
        var cancelado = await EmitirAsync(gestor, par2, "2031-08-01", 777m, 0m);
        var cancelamento = await gestor.PostAsJsonAsync($"/api/relatorios/{cancelado.GetProperty("id").GetGuid()}/cancelar", new { versao = cancelado.GetProperty("versao").GetString(), motivo = "Teste" });
        Assert.Equal(HttpStatusCode.OK, cancelamento.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await gestor.PostAsJsonAsync("/api/relatorios", new
        {
            clienteId = par1.ClienteId, equipamentoId = par1.EquipamentoId, tipoServico = "Corretiva", dataServico = "2031-09-01", total = 555m, desconto = 0m
        })).StatusCode);

        var resposta = await gestor.GetAsync($"/api/gerencial/indicadores?{Periodo("2031")}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());
        var corpo = await LerAsync(resposta);
        var totais = corpo.GetProperty("totais");
        Assert.Equal(3, totais.GetProperty("quantidade").GetInt32());
        Assert.Equal(350m, totais.GetProperty("total").GetDecimal());
        Assert.Equal(10m, totais.GetProperty("descontos").GetDecimal());
        Assert.Equal(340m, totais.GetProperty("totalGeral").GetDecimal());
        Assert.Equal(113.33m, totais.GetProperty("ticketMedio").GetDecimal());

        var porExecutor = corpo.GetProperty("porExecutor").EnumerateArray().ToList();
        Assert.Equal(2, porExecutor.Count);
        Assert.Equal(tecnicoId, porExecutor[0].GetProperty("tecnicoId").GetGuid());      // técnico 250 > gestor 90
        Assert.Equal(250m, porExecutor[0].GetProperty("totalGeral").GetDecimal());
        Assert.Equal(2, porExecutor[0].GetProperty("quantidade").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(porExecutor[0].GetProperty("tecnicoNome").GetString()));
        Assert.Equal(90m, porExecutor[1].GetProperty("totalGeral").GetDecimal());

        var porTipo = corpo.GetProperty("porTipo").EnumerateArray().ToList();
        Assert.Equal(["Corretiva", "Preventiva"], porTipo.Select(t => t.GetProperty("tipo").GetString()).ToArray());
        Assert.Equal(140m, porTipo[0].GetProperty("totalGeral").GetDecimal());
        Assert.Equal(2, porTipo[0].GetProperty("quantidade").GetInt32());
        Assert.Equal(200m, porTipo[1].GetProperty("totalGeral").GetDecimal());

        var clientes = corpo.GetProperty("maioresClientes").EnumerateArray().ToList();
        Assert.Equal(par2.ClienteId, clientes[0].GetProperty("clienteId").GetGuid());    // cliente 2: 200 > cliente 1: 140
        Assert.Equal(200m, clientes[0].GetProperty("totalGeral").GetDecimal());
        Assert.Equal(140m, clientes[1].GetProperty("totalGeral").GetDecimal());
        Assert.False(string.IsNullOrWhiteSpace(clientes[0].GetProperty("clienteNome").GetString()));
    }

    [FactBanco]
    public async Task Indicadores_RetificadoContaPeloValorVigente()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        var par = await NovoParAsync(gestorId);
        var emitido = await EmitirAsync(gestor, par, "2033-03-03", 100m, 0m);

        var retificado = await gestor.PostAsJsonAsync($"/api/relatorios/{emitido.GetProperty("id").GetGuid()}/retificar", new
        {
            versao = emitido.GetProperty("versao").GetString(), motivo = "Valor errado", atualizarDadosDeCadastro = false, tipoServico = "Corretiva",
            dataServico = "2033-03-03", servicoExecutado = "Serviço de teste", garantiaDias = 30, total = 180m, desconto = 20m
        });
        Assert.Equal(HttpStatusCode.OK, retificado.StatusCode);

        var corpo = await LerAsync(await gestor.GetAsync($"/api/gerencial/indicadores?{Periodo("2033")}"));

        Assert.Equal(1, corpo.GetProperty("totais").GetProperty("quantidade").GetInt32());
        Assert.Equal(160m, corpo.GetProperty("totais").GetProperty("totalGeral").GetDecimal());
        Assert.Equal(20m, corpo.GetProperty("totais").GetProperty("descontos").GetDecimal());
    }

    [FactBanco]
    public async Task Indicadores_SemDados_DevolveZerosEListasVazias_ComTicketZero()
    {
        var (gestor, _) = await LogarAsync(PerfilUsuario.Gestor);

        var corpo = await LerAsync(await gestor.GetAsync($"/api/gerencial/indicadores?{Periodo("2040")}"));

        Assert.Equal(0, corpo.GetProperty("totais").GetProperty("quantidade").GetInt32());
        Assert.Equal(0m, corpo.GetProperty("totais").GetProperty("totalGeral").GetDecimal());
        Assert.Equal(0m, corpo.GetProperty("totais").GetProperty("ticketMedio").GetDecimal());
        Assert.Empty(corpo.GetProperty("porExecutor").EnumerateArray());
        Assert.Empty(corpo.GetProperty("porTipo").EnumerateArray());
        Assert.Empty(corpo.GetProperty("maioresClientes").EnumerateArray());
    }

    [FactBanco]
    public async Task Indicadores_PeriodoInvertido_Retorna400()
    {
        var (gestor, _) = await LogarAsync(PerfilUsuario.Gestor);

        var resposta = await gestor.GetAsync("/api/gerencial/indicadores?de=2031-12-31&ate=2031-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [FactBanco]
    public async Task Indicadores_MaioresClientes_SaoNoMaximoDez()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        for (var i = 1; i <= 12; i++) await EmitirAsync(gestor, await NovoParAsync(gestorId), "2034-04-04", i * 10m, 0m);

        var corpo = await LerAsync(await gestor.GetAsync($"/api/gerencial/indicadores?{Periodo("2034")}"));

        var clientes = corpo.GetProperty("maioresClientes").EnumerateArray().ToList();
        Assert.Equal(10, clientes.Count);
        Assert.Equal(120m, clientes[0].GetProperty("totalGeral").GetDecimal());
        Assert.Equal(30m, clientes[9].GetProperty("totalGeral").GetDecimal());
        Assert.Equal(12, corpo.GetProperty("totais").GetProperty("quantidade").GetInt32());
    }

    private static string Planilha(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        using var leitor = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return leitor.ReadToEnd();
    }

    [FactBanco]
    public async Task Exportar_GeraXlsxSoComEmitidos_SemCpfCnpj_ComSnapshots()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        var par = await NovoParAsync(gestorId);
        var emitido = await EmitirAsync(gestor, par, "2035-06-15", 120.5m, 20.5m);
        var documento = emitido.GetProperty("cliente").GetProperty("documento").GetString()!;
        var nomeDoCliente = emitido.GetProperty("cliente").GetProperty("nome").GetString()!;
        var cancelado = await EmitirAsync(gestor, par, "2035-06-16", 999m, 0m);
        await gestor.PostAsJsonAsync($"/api/relatorios/{cancelado.GetProperty("id").GetGuid()}/cancelar", new { versao = cancelado.GetProperty("versao").GetString(), motivo = "Teste" });

        var resposta = await gestor.GetAsync($"/api/gerencial/exportar-relatorios?{Periodo("2035")}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", resposta.Content.Headers.ContentType?.MediaType);
        Assert.Equal("relatorios-emitidos-2035-01-01-a-2035-12-31.xlsx", resposta.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());

        var xml = Planilha(await resposta.Content.ReadAsByteArrayAsync());
        Assert.Contains(emitido.GetProperty("numeroFormatado").GetString()!.TrimStart('0'), xml);
        Assert.Contains(nomeDoCliente, xml);
        Assert.Contains("<v>120.5</v>", xml);
        Assert.Contains("<v>20.5</v>", xml);
        Assert.Contains("<v>100</v>", xml);                       // total geral = 120,5 - 20,5
        Assert.DoesNotContain("999", xml);                         // o cancelado fica de fora
        Assert.DoesNotContain(documento, xml);                     // CPF/CNPJ nunca vai para a planilha
        Assert.DoesNotContain(documento.Insert(2, "."), xml);
    }

    [FactBanco]
    public async Task Exportar_FiltraPorExecutorTipoECliente()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        var (tecnico, tecnicoId) = await LogarAsync(PerfilUsuario.Tecnico);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        var par1 = await NovoParAsync(gestorId);
        var par2 = await NovoParAsync(gestorId);
        await EmitirAsync(gestor, par1, "2036-01-01", 1111.11m, 0m);
        await EmitirAsync(tecnico, par2, "2036-01-02", 2222.22m, 0m, "Preventiva");

        var porExecutor = Planilha(await (await gestor.GetAsync($"/api/gerencial/exportar-relatorios?{Periodo("2036")}&tecnicoId={tecnicoId}")).Content.ReadAsByteArrayAsync());
        var porTipo = Planilha(await (await gestor.GetAsync($"/api/gerencial/exportar-relatorios?{Periodo("2036")}&tipo=Corretiva")).Content.ReadAsByteArrayAsync());
        var porCliente = Planilha(await (await gestor.GetAsync($"/api/gerencial/exportar-relatorios?{Periodo("2036")}&clienteId={par2.ClienteId}")).Content.ReadAsByteArrayAsync());

        Assert.Contains("<v>2222.22</v>", porExecutor);
        Assert.DoesNotContain("<v>1111.11</v>", porExecutor);
        Assert.Contains("<v>1111.11</v>", porTipo);
        Assert.DoesNotContain("<v>2222.22</v>", porTipo);
        Assert.Contains("<v>2222.22</v>", porCliente);
        Assert.DoesNotContain("<v>1111.11</v>", porCliente);
    }

    [FactBanco]
    public async Task Exportar_SemRelatorios_GeraSoOCabecalho()
    {
        var (gestor, _) = await LogarAsync(PerfilUsuario.Gestor);

        var resposta = await gestor.GetAsync($"/api/gerencial/exportar-relatorios?{Periodo("2041")}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var xml = Planilha(await resposta.Content.ReadAsByteArrayAsync());
        Assert.Contains("Data do serviço", xml);
        Assert.DoesNotContain("<row r=\"2\"", xml);
    }

    [FactBanco]
    public async Task Exportar_PeriodoInvertido_Retorna400()
    {
        var (gestor, _) = await LogarAsync(PerfilUsuario.Gestor);

        Assert.Equal(HttpStatusCode.BadRequest, (await gestor.GetAsync("/api/gerencial/exportar-relatorios?de=2031-12-31&ate=2031-01-01")).StatusCode);
    }

    [FactBanco]
    public async Task Exportar_NomeDoClienteComCaracteresEspeciais_NaoQuebraAPlanilha()
    {
        var (gestor, gestorId) = await LogarAsync(PerfilUsuario.Gestor);
        await CadastrosDeTeste.GarantirEmissorAsync(_fabrica.Services, gestorId);
        var par = await NovoParAsync(gestorId);
        var clienteHttp = await gestor.GetAsync($"/api/clientes/{par.ClienteId}");
        var cliente = await LerAsync(clienteHttp);
        var corpo = new Dictionary<string, object?>
        {
            ["nomeRazaoSocial"] = "=SOMA(A1) <Mercado> & \"Filhos\" ção",
            ["documento"] = cliente.GetProperty("documento").GetString(),
            ["versao"] = cliente.GetProperty("versao").GetString()
        };
        Assert.Equal(HttpStatusCode.OK, (await gestor.PutAsJsonAsync($"/api/clientes/{par.ClienteId}", corpo)).StatusCode);
        await EmitirAsync(gestor, par, "2037-07-07", 33m, 0m);

        var resposta = await gestor.GetAsync($"/api/gerencial/exportar-relatorios?{Periodo("2037")}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var xml = Planilha(await resposta.Content.ReadAsByteArrayAsync());
        Assert.Contains("t=\"inlineStr\"", xml);
        Assert.Contains("=SOMA(A1) &lt;Mercado&gt; &amp; \"Filhos\" ção", xml);
        Assert.DoesNotContain("<f>", xml);
    }
}
