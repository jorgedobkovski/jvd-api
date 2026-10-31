using System.Globalization;
using System.Text.RegularExpressions;
using Jvd.Application.Catalogo;
using Jvd.Domain;

namespace Jvd.Application.Importacao;

public sealed record ServicoParaImportar(int Linha, DadosDoServico Dados, bool Ativo);

public sealed record InterpretacaoDeServicos(
    bool EstruturaValida,
    bool ViuGrupoDeServicos,
    IReadOnlyList<ServicoParaImportar> Servicos,
    IReadOnlyList<Ocorrencia> Ocorrencias,
    int Lidos,
    int Ignoradas);

/// <summary>
/// Lê o grupo SERVIÇOS da planilha de mercadorias do ERP (Relatorio211). Regra pura, sem banco: decisões em
/// Specs/IMPORTACAO.md. As mensagens são fixas.
/// </summary>
public static partial class InterpretadorDeServicos
{
    private const string ColDescricao = "A", ColPreco = "I", ColInativo = "O";
    private const decimal PrecoMaximo = 9_999_999_999.99m;

    [GeneratedRegex(@"^\s*(\d+)\s*-\s*(.+?)\s*$")]
    private static partial Regex CodigoEDescricao();

    public static InterpretacaoDeServicos Interpretar(IEnumerable<LinhaDaPlanilha> linhas)
    {
        var servicos = new List<ServicoParaImportar>();
        var ocorrencias = new List<Ocorrencia>();
        var lidos = 0;
        var ignoradas = 0;
        var viuCabecalho = false;
        var viuGrupoDeServicos = false;
        var grupoAtual = (string?)null;

        foreach (var linha in linhas)
        {
            var descricaoDaLinha = linha.Valor(ColDescricao);

            if (EhCabecalho(linha))
            {
                viuCabecalho = true;
                ignoradas++;
                continue;
            }

            if (descricaoDaLinha is not null && Normalizar(descricaoDaLinha)!.StartsWith("GRUPO:", StringComparison.Ordinal))
            {
                grupoAtual = Normalizar(descricaoDaLinha)!["GRUPO:".Length..].Trim();
                viuGrupoDeServicos |= grupoAtual == "SERVICOS";
                continue; // cabeçalho de grupo não é item nem ruído
            }

            var ehItemDoGrupo = viuCabecalho && grupoAtual == "SERVICOS" && descricaoDaLinha is not null
                                && CodigoEDescricao().IsMatch(descricaoDaLinha);

            if (!ehItemDoGrupo)
            {
                if (linha.Celulas.Count > 0) ignoradas++; // subgrupo, outros grupos, totais; linha em branco não conta
                continue;
            }

            lidos++;
            var aviso = (string motivo) => ocorrencias.Add(new Ocorrencia(linha.Numero, GravidadeOcorrencia.Aviso, motivo));
            var partes = CodigoEDescricao().Match(descricaoDaLinha!);

            var codigo = Cortar(partes.Groups[1].Value, 40, "Código de origem cortado em 40 caracteres.", aviso);
            var descricao = Cortar(partes.Groups[2].Value, 200, "Descrição cortada em 200 caracteres.", aviso)!;
            var preco = Preco(linha.Valor(ColPreco), aviso);
            var ativo = EstaAtivo(linha.Valor(ColInativo), aviso);

            servicos.Add(new ServicoParaImportar(linha.Numero, new DadosDoServico(descricao, codigo, preco), ativo));
        }

        return new InterpretacaoDeServicos(viuCabecalho, viuGrupoDeServicos, servicos, ocorrencias, lidos, ignoradas);
    }

    private static bool EhCabecalho(LinhaDaPlanilha l) =>
        Normalizar(l.Valor(ColDescricao)) == "DESCRICAO" && Normalizar(l.Valor(ColPreco)) == "PRECO R$";

    /// <summary>Preço em texto brasileiro ("1.234,56"). Qualquer coisa fora do aceito pelo catálogo vira "sem preço" com aviso.</summary>
    private static decimal? Preco(string? texto, Action<string> aviso)
    {
        if (texto is null)
        {
            aviso("Sem preço: importado sem preço sugerido.");
            return null;
        }

        var padronizado = texto.Replace(".", "").Replace(',', '.');
        if (!decimal.TryParse(padronizado, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var valor)
            || valor < 0 || valor > PrecoMaximo || decimal.Round(valor, 2) != valor)
        {
            aviso("Preço ilegível ou fora do aceito: importado sem preço sugerido.");
            return null;
        }

        return valor;
    }

    private static bool EstaAtivo(string? texto, Action<string> aviso)
    {
        switch (Normalizar(texto))
        {
            case "SIM":
                return false;
            case null:
            case "NAO":
                return true;
            default:
                aviso("Valor de 'Inativo' não reconhecido: importado como ativo.");
                return true;
        }
    }

    private static string? Cortar(string? valor, int maximo, string motivo, Action<string> aviso)
    {
        if (valor is null || valor.Length <= maximo) return valor;

        aviso(motivo);
        return valor[..maximo].TrimEnd();
    }

    private static string? Normalizar(string? texto) => InterpretadorDeClientes.Normalizar(texto);
}
