using Jvd.Application.Comum;
using Jvd.Application.Emissor;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Jvd.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Cadastros;

/// <summary>
/// Regras do emissor (registro único) sobre SQL Server real. Cada teste usa o PRÓPRIO banco descartável, porque o
/// emissor é um singleton. Dados SINTÉTICOS. Só com JVD_TESTES_BANCO=1.
/// </summary>
public class EmissorServiceTests
{
    private const string Cnpj = "11222333000181";

    private sealed class Cenario(BancoDeTeste banco, ServiceProvider sp, Guid ator) : IAsyncDisposable
    {
        public ServiceProvider Sp { get; } = sp;
        public Guid Ator { get; } = ator;
        public RelogioFalso Relogio { get; init; } = null!;

        public async Task<T> Servico<T>(Func<IEmissorService, Task<T>> acao)
        {
            await using var escopo = Sp.CreateAsyncScope();
            return await acao(escopo.ServiceProvider.GetRequiredService<IEmissorService>());
        }

        public async Task<List<EventoDeCadastro>> EventosAsync()
        {
            await using var escopo = Sp.CreateAsyncScope();
            var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
            return await db.EventosDeCadastro.AsNoTracking().Where(e => e.Entidade == "emissor").OrderBy(e => e.OcorridoEm).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Sp.DisposeAsync();
            await banco.DisposeAsync();
        }
    }

    private static async Task<Cenario> NovoCenarioAsync()
    {
        var banco = await BancoDeTeste.CriarAsync();
        var relogio = new RelogioFalso(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var sp = ServicosDeTeste.Construir(banco.Conexao, relogio);
        var ator = (await ServicosDeTeste.CriarUsuarioAsync(sp, PerfilUsuario.Gestor)).Id;
        return new Cenario(banco, sp, ator) { Relogio = relogio };
    }

    private static DadosDoEmissor Dados(string razao = "Empresa Sintética Ltda", string cnpj = Cnpj) =>
        new(razao, null, cnpj, null, null, null, null, null, null, null, null, null, null, null);

    private static string? Codigo(Resultado r) => r.Erros.FirstOrDefault()?.Codigo;

    [FactBanco]
    public async Task Obter_NuncaConfigurado_Retorna404()
    {
        await using var c = await NovoCenarioAsync();

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await c.Servico(s => s.ObterAsync())));
    }

    [FactBanco]
    public async Task PrimeiraGravacao_Cria_NormalizaDocumentosEGravaEvento()
    {
        await using var c = await NovoCenarioAsync();
        var cpf = DadosDeTeste.NovoCpf();
        var dados = Dados() with
        {
            Cnpj = "11.222.333/0001-81", Cep = "76.800-000", Uf = " ro ", ResponsavelTecnicoNome = "Fulano Responsável",
            ResponsavelTecnicoCpf = cpf, NumeroAutorizacaoIpem = "IPEM-123"
        };

        var r = await c.Servico(s => s.SalvarAsync(dados, null, c.Ator));

        Assert.True(r.Sucesso);
        Assert.Equal(Cnpj, r.Valor!.Cnpj);
        Assert.Equal("76800000", r.Valor.Cep);
        Assert.Equal("RO", r.Valor.Uf);
        Assert.Equal(cpf, r.Valor.ResponsavelTecnicoCpf);
        Assert.False(string.IsNullOrEmpty(r.Valor.Versao));

        var lido = await c.Servico(s => s.ObterAsync());
        Assert.True(lido.Sucesso);

        var evento = Assert.Single(await c.EventosAsync());
        Assert.Equal(TipoEventoCadastro.Criado, evento.Tipo);
        Assert.Equal(c.Ator, evento.AtorId);
        Assert.Contains("responsavel_tecnico_cpf", evento.Campos);
        Assert.DoesNotContain(cpf, evento.Campos);
        Assert.DoesNotContain("IPEM-123", evento.Campos);
    }

    [FactBanco]
    public async Task Gravacao_DadosInvalidos_ListaErros_SemEcoarValores()
    {
        await using var c = await NovoCenarioAsync();
        var dados = new DadosDoEmissor(" ", null, "123", null, null, null, null, "12", null, "ZZ", null, null, null, "999.999.999-99");

        var r = await c.Servico(s => s.SalvarAsync(dados, null, c.Ator));

        Assert.False(r.Sucesso);
        Assert.All(r.Erros, e => Assert.Equal(CodigosDeErro.Validacao, e.Codigo));
        Assert.Equal(5, r.Erros.Count);
        Assert.DoesNotContain(r.Erros, e => e.Mensagem.Contains("999") || e.Mensagem.Contains("ZZ"));
        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(await c.Servico(s => s.ObterAsync())));
    }

    [FactBanco]
    public async Task Gravacao_CpfNoLugarDoCnpj_EhRecusado()
    {
        await using var c = await NovoCenarioAsync();

        var r = await c.Servico(s => s.SalvarAsync(Dados(cnpj: DadosDeTeste.NovoCpf()), null, c.Ator));

        Assert.Equal(CodigosDeErro.Validacao, Codigo(r));
    }

    [FactBanco]
    public async Task Gravacao_ExcedendoTamanhos_EhRecusado()
    {
        await using var c = await NovoCenarioAsync();
        var dados = Dados() with { Telefones = new string('9', 201), NumeroAutorizacaoIpem = new string('x', 61), ResponsavelTecnicoNome = new string('n', 151) };

        var r = await c.Servico(s => s.SalvarAsync(dados, null, c.Ator));

        Assert.Equal(3, r.Erros.Count);
    }

    [FactBanco]
    public async Task Alterar_ExigeAVersao_EVersaoVelhaDa409()
    {
        await using var c = await NovoCenarioAsync();
        var criado = (await c.Servico(s => s.SalvarAsync(Dados(), null, c.Ator))).Valor!;

        var semVersao = await c.Servico(s => s.SalvarAsync(Dados("Outro Nome"), null, c.Ator));
        Assert.Equal(CodigosDeErro.Conflito, Codigo(semVersao));

        var malformada = await c.Servico(s => s.SalvarAsync(Dados("Outro Nome"), "isto não é base64", c.Ator));
        Assert.Equal(CodigosDeErro.Validacao, Codigo(malformada));

        var primeira = await c.Servico(s => s.SalvarAsync(Dados("Primeiro Nome"), criado.Versao, c.Ator));
        Assert.True(primeira.Sucesso);
        Assert.NotEqual(criado.Versao, primeira.Valor!.Versao);

        var velha = await c.Servico(s => s.SalvarAsync(Dados("Segundo Nome"), criado.Versao, c.Ator));
        Assert.Equal(CodigosDeErro.Conflito, Codigo(velha));
        Assert.Equal("Primeiro Nome", (await c.Servico(s => s.ObterAsync())).Valor!.RazaoSocial);
    }

    [FactBanco]
    public async Task Alterar_RegistraSoNomesDosCamposAlterados_ESemMudancaNaoGeraEvento()
    {
        await using var c = await NovoCenarioAsync();
        var criado = (await c.Servico(s => s.SalvarAsync(Dados(), null, c.Ator))).Valor!;

        var igual = await c.Servico(s => s.SalvarAsync(Dados(), criado.Versao, c.Ator));
        Assert.True(igual.Sucesso);
        Assert.Equal(criado.Versao, igual.Valor!.Versao);
        Assert.Single(await c.EventosAsync());

        c.Relogio.Avancar(TimeSpan.FromMinutes(5));
        var editado = await c.Servico(s => s.SalvarAsync(Dados("Nova Razão") with { Telefones = "(69) 0000-0000" }, criado.Versao, c.Ator));
        Assert.True(editado.Sucesso);

        var evento = (await c.EventosAsync()).Single(e => e.Tipo == TipoEventoCadastro.Editado);
        Assert.Equal("razao_social,telefones", evento.Campos);
    }

    [FactBanco]
    public async Task VersaoSemEmissorCadastrado_Retorna404()
    {
        await using var c = await NovoCenarioAsync();

        var r = await c.Servico(s => s.SalvarAsync(Dados(), "AAAAAAAAB9E=", c.Ator));

        Assert.Equal(CodigosDeErro.NaoEncontrado, Codigo(r));
    }

    [FactBanco]
    public async Task PrimeirasGravacoesSimultaneas_UmaVence_AOutraRecebe409_ESoExisteUmEmissor()
    {
        await using var c = await NovoCenarioAsync();

        var tarefas = Enumerable.Range(0, 4)
            .Select(i => Task.Run(() => c.Servico(s => s.SalvarAsync(Dados("Empresa " + i), null, c.Ator))))
            .ToArray();
        var resultados = await Task.WhenAll(tarefas);

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        Assert.All(resultados.Where(r => !r.Sucesso), r => Assert.Equal(CodigosDeErro.Conflito, Codigo(r)));

        await using var escopo = c.Sp.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<JvdDbContext>();
        Assert.Equal(1, await db.Emissores.CountAsync());
    }
}
