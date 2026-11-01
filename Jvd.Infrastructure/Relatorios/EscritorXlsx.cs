using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Jvd.Infrastructure.Relatorios;

/// <summary>
/// Gerador mínimo de planilha .xlsx (uma aba, cabeçalho em negrito e congelado, filtro, datas e valores formatados).
/// Escrito à mão sobre <c>System.IO.Compression</c> para não somar dependência. Todo texto sai como <c>inlineStr</c>
/// (string literal): o Excel NUNCA o trata como fórmula, então um nome começando por "=" ou "@" não vira macro.
/// Caracteres que o XML não aceita são trocados por espaço; células passam de 32.000 caracteres são cortadas.
/// </summary>
public static class EscritorXlsx
{
    private const int LimiteDeTextoDaCelula = 32_000;
    private const int EstiloCabecalho = 1;
    private const int EstiloData = 2;
    private const int EstiloDinheiro = 3;

    public static byte[] Escrever(string nomeDaAba, IReadOnlyList<string> cabecalho, IEnumerable<IReadOnlyList<object?>> linhas)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entrada(zip, "[Content_Types].xml", TiposDeConteudo);
            Entrada(zip, "_rels/.rels", RelacoesDoPacote);
            Entrada(zip, "xl/workbook.xml", Workbook(nomeDaAba));
            Entrada(zip, "xl/_rels/workbook.xml.rels", RelacoesDoWorkbook);
            Entrada(zip, "xl/styles.xml", Estilos);
            EntradaDaPlanilha(zip, cabecalho, linhas);
        }

        return memoria.ToArray();
    }

    private static void Entrada(ZipArchive zip, string caminho, string conteudo)
    {
        var entrada = zip.CreateEntry(caminho, CompressionLevel.Optimal);
        using var fluxo = entrada.Open();
        using var escritor = new StreamWriter(fluxo, new UTF8Encoding(false));
        escritor.Write(conteudo);
    }

    private static void EntradaDaPlanilha(ZipArchive zip, IReadOnlyList<string> cabecalho, IEnumerable<IReadOnlyList<object?>> linhas)
    {
        var entrada = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var fluxo = entrada.Open();
        using var xml = XmlWriter.Create(fluxo, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = true });

        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("worksheet", ns);

        xml.WriteStartElement("sheetViews");
        xml.WriteStartElement("sheetView");
        xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane");
        xml.WriteAttributeString("ySplit", "1");
        xml.WriteAttributeString("topLeftCell", "A2");
        xml.WriteAttributeString("activePane", "bottomLeft");
        xml.WriteAttributeString("state", "frozen");
        xml.WriteEndElement(); // pane
        xml.WriteEndElement(); // sheetView
        xml.WriteEndElement(); // sheetViews

        xml.WriteStartElement("cols");
        for (var i = 0; i < cabecalho.Count; i++)
        {
            xml.WriteStartElement("col");
            xml.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("width", Math.Max(14, cabecalho[i].Length + 4).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("customWidth", "1");
            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // cols

        xml.WriteStartElement("sheetData");
        EscreverLinha(xml, 1, cabecalho.Cast<object?>().ToList(), cabecalho: true);
        var numero = 2;
        foreach (var linha in linhas) EscreverLinha(xml, numero++, linha, cabecalho: false);
        xml.WriteEndElement(); // sheetData

        xml.WriteStartElement("autoFilter");
        xml.WriteAttributeString("ref", $"A1:{Coluna(cabecalho.Count - 1)}{Math.Max(1, numero - 1)}");
        xml.WriteEndElement();

        xml.WriteEndElement(); // worksheet
        xml.WriteEndDocument();
    }

    private static void EscreverLinha(XmlWriter xml, int numero, IReadOnlyList<object?> celulas, bool cabecalho)
    {
        xml.WriteStartElement("row");
        xml.WriteAttributeString("r", numero.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < celulas.Count; i++) EscreverCelula(xml, $"{Coluna(i)}{numero}", celulas[i], cabecalho);
        xml.WriteEndElement();
    }

    private static void EscreverCelula(XmlWriter xml, string referencia, object? valor, bool cabecalho)
    {
        switch (valor)
        {
            case null:
                return; // célula vazia: nada a gravar
            case DateOnly data:
                Numero(xml, referencia, (data.ToDateTime(TimeOnly.MinValue) - new DateTime(1899, 12, 30)).Days.ToString(CultureInfo.InvariantCulture), EstiloData);
                return;
            case decimal dinheiro:
                Numero(xml, referencia, dinheiro.ToString("0.####", CultureInfo.InvariantCulture), EstiloDinheiro); // 22,00 → "22"; 120,50 → "120.5"
                return;
            case int inteiro:
                Numero(xml, referencia, inteiro.ToString(CultureInfo.InvariantCulture), 0);
                return;
            default:
                Texto(xml, referencia, valor.ToString() ?? "", cabecalho ? EstiloCabecalho : 0);
                return;
        }
    }

    private static void Numero(XmlWriter xml, string referencia, string valor, int estilo)
    {
        xml.WriteStartElement("c");
        xml.WriteAttributeString("r", referencia);
        if (estilo != 0) xml.WriteAttributeString("s", estilo.ToString(CultureInfo.InvariantCulture));
        xml.WriteElementString("v", valor);
        xml.WriteEndElement();
    }

    private static void Texto(XmlWriter xml, string referencia, string texto, int estilo)
    {
        xml.WriteStartElement("c");
        xml.WriteAttributeString("r", referencia);
        if (estilo != 0) xml.WriteAttributeString("s", estilo.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("t", "inlineStr");
        xml.WriteStartElement("is");
        xml.WriteStartElement("t");
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteString(Higienizar(texto));
        xml.WriteEndElement(); // t
        xml.WriteEndElement(); // is
        xml.WriteEndElement(); // c
    }

    /// <summary>Troca por espaço o que o XML não aceita (controles) e corta o excesso; o resto passa intacto.</summary>
    public static string Higienizar(string texto)
    {
        var corte = texto.Length > LimiteDeTextoDaCelula ? texto[..LimiteDeTextoDaCelula] : texto;
        var saida = new StringBuilder(corte.Length);
        foreach (var rune in corte.EnumerateRunes())
        {
            var valido = rune.Value > 0xFFFF || XmlConvert.IsXmlChar((char)rune.Value);
            saida.Append(valido ? rune.ToString() : " ");
        }

        return saida.ToString();
    }

    /// <summary>0 → A, 25 → Z, 26 → AA…</summary>
    public static string Coluna(int indice)
    {
        var nome = "";
        for (var n = indice; n >= 0; n = n / 26 - 1) nome = (char)('A' + n % 26) + nome;
        return nome;
    }

    private static string Workbook(string nomeDaAba)
    {
        var nome = new string(Higienizar(nomeDaAba).Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        nome = nome.Length > 31 ? nome[..31] : nome;
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{System.Security.SecurityElement.Escape(nome)}" sheetId="1" r:id="rId1"/></sheets></workbook>""";
    }

    private const string TiposDeConteudo =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""";

    private const string RelacoesDoPacote =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""";

    private const string RelacoesDoWorkbook =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""";

    // Estilos: 0 padrão · 1 cabeçalho em negrito · 2 data dd/mm/aaaa · 3 dinheiro 0,00 (formato embutido 4: #,##0.00)
    private const string Estilos =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="4"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""";
}
