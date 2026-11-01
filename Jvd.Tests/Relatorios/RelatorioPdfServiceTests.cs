using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Jvd.Infrastructure.Relatorios;
using UglyToad.PdfPig;

namespace Jvd.Tests.Relatorios;

/// <summary>PDF do relatório (Fase 5): conteúdo, rótulos de retificado/cancelado/via e recusa de rascunho. Dados sintéticos.</summary>
public class RelatorioPdfServiceTests
{
    private static readonly AtorDoRelatorio Ator = new(Guid.NewGuid(), EhGestor: true);

    private sealed class RelatoriosFalsos(Resultado<RelatorioDetalhe> resposta) : IRelatorioService
    {
        public Task<Resultado<RelatorioDetalhe>> ObterAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default) => Task.FromResult(resposta);

        public Task<PaginaDeResultados<RelatorioResumo>> ListarAsync(FiltroDeRelatorios filtro, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resultado<RelatorioDetalhe>> CriarRascunhoAsync(DadosDoRelatorio dados, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resultado<RelatorioDetalhe>> AtualizarRascunhoAsync(Guid id, DadosDoRelatorio dados, string versao, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resultado> DescartarRascunhoAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resultado<RelatorioDetalhe>> EmitirAsync(Guid id, string versao, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resultado<RelatorioDetalhe>> RetificarAsync(Guid id, DadosDaRetificacao dados, string versao, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resultado<RelatorioDetalhe>> CancelarAsync(Guid id, string? motivo, string versao, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resultado<IReadOnlyList<RevisaoDoRelatorio>>> ListarRevisoesAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static RelatorioDetalhe Relatorio(StatusRelatorio status = StatusRelatorio.Emitido, int revisao = 0, bool comSnapshots = true) => new(
        Id: Guid.NewGuid(), Numero: 42, NumeroFormatado: "000042", Status: status, TipoServico: TipoServico.Corretiva,
        DataServico: new DateOnly(2026, 10, 30), ClienteId: Guid.NewGuid(), ClienteNome: "Mercado Teste", EquipamentoId: Guid.NewGuid(),
        EquipamentoDescricao: "Balança Teste", TecnicoId: Guid.NewGuid(), TecnicoNome: "Técnico Sintético",
        ServicoExecutado: "Troca da célula de carga", Observacoes: "Sem observações extras", VerificacaoAnteriorData: new DateOnly(2025, 10, 1),
        VerificacaoAnteriorCertificado: "CERT-123", NumeroSeloReparador: "SELO-9", GarantiaDias: 30, GarantiaExtenso: Extenso.Garantia(30),
        Total: 150m, Desconto: 10m, TotalGeral: 140m, CatalogoServicoId: null, Revisao: revisao,
        Cliente: comSnapshots ? new SnapshotDoCliente("Mercado Teste Ltda", null, "000.000.000-00", "RG-1", "Rua Teste", "10", "Centro", "76800-000", "Porto Velho", "RO", "(69) 0000-0000") : null,
        Equipamento: comSnapshots ? new SnapshotDoEquipamento("Balança Teste", "SERIE-1", "INMETRO-1") : null,
        Emissor: comSnapshots ? new SnapshotDoEmissor("Empresa Teste Ltda", "Teste Balanças", "00.000.000/0001-00", "123", "Av. Teste", "1", "Centro", "76800-001", "Porto Velho", "RO", "(69) 1111-1111", "IPEM-77", "Responsável Sintético", "111.111.111-11") : null,
        EmitidoEm: DateTime.UtcNow, EmitidoPor: Guid.NewGuid(), CanceladoEm: null, CanceladoPor: null, MotivoCancelamento: null,
        CriadoEm: DateTime.UtcNow, AtualizadoEm: DateTime.UtcNow, Versao: "AAAA");

    private static async Task<(Resultado<ArquivoPdf> Resultado, string Texto)> GerarAsync(RelatorioDetalhe r, ViaDoRelatorio via = ViaDoRelatorio.Cliente)
    {
        var servico = new RelatorioPdfService(new RelatoriosFalsos(Resultado<RelatorioDetalhe>.Ok(r)));
        var resultado = await servico.GerarAsync(r.Id, via, Ator);
        if (!resultado.Sucesso) return (resultado, "");

        using var pdf = PdfDocument.Open(resultado.Valor!.Conteudo);
        return (resultado, string.Join("\n", pdf.GetPages().Select(p => p.Text)));
    }

    [Fact]
    public async Task Emitido_GeraPdfValido_ComOsDadosDoTalao()
    {
        var (resultado, texto) = await GerarAsync(Relatorio());

        Assert.True(resultado.Sucesso);
        Assert.Equal("relatorio-000042.pdf", resultado.Valor!.NomeDoArquivo);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(resultado.Valor.Conteudo, 0, 4));
        foreach (var esperado in new[] { "Empresa Teste Ltda", "00.000.000/0001-00", "IPEM-77", "000042", "Mercado Teste Ltda", "000.000.000-00",
                     "Balança Teste", "SERIE-1", "INMETRO-1", "Troca da célula de carga", "SELO-9", "CERT-123", "Garantia: 30 dias (trinta)", "Técnico Sintético",
                     "Responsável Sintético", "Ass. do Técnico", "Ass. do Cliente", "Autorizo a execução" })
            Assert.Contains(esperado, texto);
    }

    [Fact]
    public async Task RelatorioComum_CabeEmUmaPagina()
    {
        var servico = new RelatorioPdfService(new RelatoriosFalsos(Resultado<RelatorioDetalhe>.Ok(Relatorio(revisao: 2))));

        var resultado = await servico.GerarAsync(Guid.NewGuid(), ViaDoRelatorio.Cliente, Ator);

        using var pdf = PdfDocument.Open(resultado.Valor!.Conteudo);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task Garantia_SaiUmaSoVez_ComConcordancia()
    {
        var (_, texto) = await GerarAsync(Relatorio());
        var (_, umDia) = await GerarAsync(Relatorio() with { GarantiaDias = 1, GarantiaExtenso = Extenso.Garantia(1) });

        Assert.DoesNotContain("30 dias (30 dias", texto);
        Assert.Contains("Garantia: 1 dia (um)", umDia);
        Assert.DoesNotContain("1 dias", umDia);
    }

    [Fact]
    public async Task VerificacaoAnteriorParcial_NaoDeixaEspacosSobrando()
    {
        var (_, soCertificado) = await GerarAsync(Relatorio() with { VerificacaoAnteriorData = null });
        var (_, soData) = await GerarAsync(Relatorio() with { VerificacaoAnteriorCertificado = null });

        Assert.Contains("Certificado: CERT-123", soCertificado);
        Assert.DoesNotContain("Verificação anterior:", soCertificado);
        Assert.Contains("Verificação anterior: 01/10/2025", soData);
        Assert.DoesNotContain("Certificado", soData);
    }

    [Fact]
    public async Task ValoresSaemEmReaisComTotalGeral()
    {
        var (_, texto) = await GerarAsync(Relatorio());

        Assert.Contains("140,00", texto);
        Assert.Contains("150,00", texto);
    }

    [Fact]
    public async Task SemRetificacao_NaoTemRotuloDeRetificadoNemDeCancelado()
    {
        var (_, texto) = await GerarAsync(Relatorio());

        Assert.DoesNotContain("RETIFICADO", texto);
        Assert.DoesNotContain("CANCELADO", texto);
    }

    [Fact]
    public async Task Retificado_IndicaARevisao()
    {
        var (_, texto) = await GerarAsync(Relatorio(revisao: 3));

        Assert.Contains("RETIFICADO", texto);
        Assert.Contains("REVISÃO 3", texto);
    }

    [Fact]
    public async Task Cancelado_IndicaCancelado_EMantemONumero()
    {
        var (resultado, texto) = await GerarAsync(Relatorio(StatusRelatorio.Cancelado));

        Assert.True(resultado.Sucesso);
        Assert.Contains("CANCELADO", texto);
        Assert.Contains("000042", texto);
    }

    [Theory]
    [InlineData(ViaDoRelatorio.Controle, "Via de controle")]
    [InlineData(ViaDoRelatorio.Cliente, "Via de cliente")]
    [InlineData(ViaDoRelatorio.Fiscalizacao, "Via de fiscalização")]
    public async Task RotuloDaVia_MudaConformeAFinalidade(ViaDoRelatorio via, string esperado)
    {
        var (_, texto) = await GerarAsync(Relatorio(), via);

        Assert.Contains(esperado, texto);
    }

    [Fact]
    public async Task Rascunho_NaoTemPdf()
    {
        var (resultado, _) = await GerarAsync(Relatorio(StatusRelatorio.Rascunho, comSnapshots: false));

        Assert.False(resultado.Sucesso);
        Assert.Equal(CodigosDeErro.RelatorioNaoEmitido, resultado.Erros[0].Codigo);
    }

    [Fact]
    public async Task RelatorioInexistenteOuAlheio_PropagaOErro_SemGerarPdf()
    {
        var naoEncontrado = Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Relatório não encontrado."));
        var servico = new RelatorioPdfService(new RelatoriosFalsos(naoEncontrado));

        var resultado = await servico.GerarAsync(Guid.NewGuid(), ViaDoRelatorio.Cliente, Ator);

        Assert.False(resultado.Sucesso);
        Assert.Equal(CodigosDeErro.NaoEncontrado, resultado.Erros[0].Codigo);
        Assert.Null(resultado.Valor);
    }

    [Fact]
    public async Task CamposOpcionaisVazios_NaoQuebramOPdf()
    {
        var r = Relatorio() with
        {
            Observacoes = null, VerificacaoAnteriorData = null, VerificacaoAnteriorCertificado = null, NumeroSeloReparador = null,
            Emissor = Relatorio().Emissor! with { NomeFantasia = null, InscricaoEstadual = null, Telefones = null, NumeroAutorizacaoIpem = null, ResponsavelTecnicoNome = null, ResponsavelTecnicoCpf = null },
            Cliente = Relatorio().Cliente! with { Documento = null, Logradouro = null, Numero = null, Bairro = null, Cep = null, Cidade = null, Uf = null, Telefone = null },
            Equipamento = new SnapshotDoEquipamento("Balança Teste", null, null)
        };

        var (resultado, texto) = await GerarAsync(r);

        Assert.True(resultado.Sucesso);
        Assert.Contains("Ass. do Cliente", texto);
    }
}
