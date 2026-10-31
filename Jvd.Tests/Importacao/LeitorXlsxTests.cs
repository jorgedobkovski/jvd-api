using System.IO.Compression;
using System.Xml.Linq;
using Jvd.Application.Importacao;
using Jvd.Infrastructure.Importacao;
using Jvd.Tests.Suporte;

namespace Jvd.Tests.Importacao;

/// <summary>O leitor trata o arquivo como NÃO CONFIÁVEL. Sem banco; arquivos 100% sintéticos.</summary>
public class LeitorXlsxTests
{
    private static readonly LeitorXlsx Leitor = new();

    private static IReadOnlyList<LinhaDaPlanilha> Ler(byte[] bytes)
    {
        using var memoria = new MemoryStream(bytes);
        return Leitor.Ler(memoria);
    }

    private static IReadOnlyDictionary<string, string> Linha(params (string Coluna, string Texto)[] celulas) =>
        celulas.ToDictionary(c => c.Coluna, c => c.Texto);

    [Fact]
    public void Le_TextoEmLinha_PorLetraDeColuna_ENumeroDaLinha()
    {
        var linhas = Ler(PlanilhaSintetica.Xlsx([Linha(("A", "um"), ("C", "tres")), PlanilhaSintetica.Vazia(), Linha(("B", "dois"))]));

        Assert.Equal(3, linhas.Count);
        Assert.Equal(1, linhas[0].Numero);
        Assert.Equal("um", linhas[0].Valor("A"));
        Assert.Equal("tres", linhas[0].Valor("C"));
        Assert.Null(linhas[0].Valor("B"));
        Assert.Empty(linhas[1].Celulas);
        Assert.Equal("dois", linhas[2].Valor("B"));
    }

    [Fact]
    public void Le_StringsCompartilhadas()
    {
        var linhas = Ler(PlanilhaSintetica.Xlsx([Linha(("A", "alfa"), ("B", "beta")), Linha(("A", "alfa"))], textoCompartilhado: true));

        Assert.Equal("alfa", linhas[0].Valor("A"));
        Assert.Equal("beta", linhas[0].Valor("B"));
        Assert.Equal("alfa", linhas[1].Valor("A"));
    }

    [Fact]
    public void Le_NumeroEFormulaComoTexto_ETextoFormatadoEmPartes()
    {
        var principal = XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var aba = new XDocument(new XElement(principal + "worksheet", new XElement(principal + "sheetData",
            new XElement(principal + "row", new XAttribute("r", 1),
                new XElement(principal + "c", new XAttribute("r", "A1"), new XElement(principal + "v", "42.5")),
                new XElement(principal + "c", new XAttribute("r", "B1"), new XAttribute("t", "str"), new XElement(principal + "v", "resultado")),
                new XElement(principal + "c", new XAttribute("r", "C1"), new XAttribute("t", "inlineStr"),
                    new XElement(principal + "is",
                        new XElement(principal + "r", new XElement(principal + "t", "pedaço ")),
                        new XElement(principal + "r", new XElement(principal + "t", "dois")),
                        new XElement(principal + "rPh", new XElement(principal + "t", "fonetica"))))))));

        var linhas = Ler(PlanilhaSintetica.Empacotar(aba));

        Assert.Equal("42.5", linhas[0].Valor("A"));
        Assert.Equal("resultado", linhas[0].Valor("B"));
        Assert.Equal("pedaço dois", linhas[0].Valor("C"));
    }

    [Fact]
    public void CelulasVaziasEDeEspacos_NaoViramConteudo()
    {
        var linhas = Ler(PlanilhaSintetica.Xlsx([Linha(("A", "   "), ("B", "x"))]));

        Assert.Null(linhas[0].Valor("A"));
        Assert.Equal("x", linhas[0].Valor("B"));
    }

    [Fact]
    public void ArquivoQueNaoEZip_ELancadoComoPlanilhaInvalida_SemEcoarOConteudo()
    {
        var lixo = System.Text.Encoding.UTF8.GetBytes("isto-nao-e-um-xlsx-SEGREDO-123");

        var erro = Assert.Throws<PlanilhaInvalidaException>(() => Ler(lixo));

        Assert.DoesNotContain("SEGREDO", erro.Message);
    }

    [Fact]
    public void ZipSemAsPartesDoXlsx_EInvalido()
    {
        var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            PlanilhaSintetica.Escrever(zip, "qualquer.txt", "nada");
        }

        Assert.Throws<PlanilhaInvalidaException>(() => Ler(memoria.ToArray()));
    }

    [Fact]
    public void XmlComDtd_EEntidadeExterna_ERecusado()
    {
        var abaMaliciosa = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]>"
                           + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                           + "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&e;</t></is></c></row></sheetData></worksheet>";
        var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            PlanilhaSintetica.Escrever(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets/></workbook>");
            PlanilhaSintetica.Escrever(zip, "xl/worksheets/sheet1.xml", abaMaliciosa);
        }

        Assert.Throws<PlanilhaInvalidaException>(() => Ler(memoria.ToArray()));
    }

    [Fact]
    public void ParteDescompactadaAcimaDoLimite_ERecusada_ContraZipBomb()
    {
        // ~65 MB de zeros comprimem para poucos KB: o arquivo é pequeno, o conteúdo descompactado não.
        var principal = XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            PlanilhaSintetica.Escrever(zip, "xl/workbook.xml", "<workbook xmlns=\"" + principal.NamespaceName + "\"><sheets/></workbook>");
            var entrada = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var escritor = new StreamWriter(entrada.Open());
            escritor.Write("<worksheet xmlns=\"" + principal.NamespaceName + "\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>");
            var bloco = new string('0', 1024 * 1024);
            for (var i = 0; i < 66; i++) escritor.Write(bloco);
            escritor.Write("</t></is></c></row></sheetData></worksheet>");
        }

        Assert.True(memoria.Length < 2 * 1024 * 1024, "o arquivo comprimido deveria ser pequeno");
        Assert.Throws<PlanilhaInvalidaException>(() => Ler(memoria.ToArray()));
    }

    [Fact]
    public void MaisLinhasQueOLimite_ERecusado()
    {
        var linhas = Enumerable.Range(0, LeitorXlsx.LimiteDeLinhas + 1).Select(_ => Linha(("A", "x")));

        Assert.Throws<PlanilhaInvalidaException>(() => Ler(PlanilhaSintetica.Xlsx(linhas)));
    }

    [Fact]
    public void TextoDeCelulaMuitoLongo_ECortadoNoLimite()
    {
        var linhas = Ler(PlanilhaSintetica.Xlsx([Linha(("A", new string('x', LeitorXlsx.LimiteDeTextoPorCelula + 500)))]));

        Assert.Equal(LeitorXlsx.LimiteDeTextoPorCelula, linhas[0].Valor("A")!.Length);
    }
}
