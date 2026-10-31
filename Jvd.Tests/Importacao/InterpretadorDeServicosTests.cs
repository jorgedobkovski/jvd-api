using Jvd.Application.Importacao;
using Jvd.Domain;
using Jvd.Tests.Suporte;

namespace Jvd.Tests.Importacao;

/// <summary>Regras de leitura do grupo SERVIÇOS (Specs/IMPORTACAO.md). Sem banco; dados sintéticos.</summary>
public class InterpretadorDeServicosTests
{
    private static InterpretacaoDeServicos Interpretar(params IReadOnlyDictionary<string, string>[] corpo)
    {
        var linhas = new IReadOnlyDictionary<string, string>[] { PlanilhaSintetica.Vazia(), PlanilhaSintetica.CabecalhoDeMercadorias() }
            .Concat(corpo).Select((c, i) => new LinhaDaPlanilha(i + 1, c));
        return InterpretadorDeServicos.Interpretar(linhas);
    }

    [Fact]
    public void LeSomenteOGrupoServicos_IgnoraOsDemaisGrupos()
    {
        var r = Interpretar(
            PlanilhaSintetica.Grupo("PECAS"),
            PlanilhaSintetica.Subgrupo(),
            PlanilhaSintetica.Item("10", "PARAFUSO", "1,50"),
            PlanilhaSintetica.Grupo("SERVIÇOS"),
            PlanilhaSintetica.Subgrupo(),
            PlanilhaSintetica.Item("316", "MÃO DE OBRA", "70,00"),
            PlanilhaSintetica.Item("564", "TROCA DO TECLADO", "180,00"),
            PlanilhaSintetica.Grupo("GERAL"),
            PlanilhaSintetica.Item("20", "OUTRA COISA", "9,00"));

        Assert.True(r.EstruturaValida);
        Assert.True(r.ViuGrupoDeServicos);
        Assert.Equal(2, r.Lidos);
        Assert.Equal(["MÃO DE OBRA", "TROCA DO TECLADO"], r.Servicos.Select(s => s.Dados.Descricao).ToArray());
        Assert.Equal(["316", "564"], r.Servicos.Select(s => s.Dados.CodigoOrigem).ToArray());
        Assert.Equal([70.00m, 180.00m], r.Servicos.Select(s => s.Dados.PrecoSugerido).ToArray());
        Assert.Empty(r.Ocorrencias);
    }

    [Fact]
    public void NomeDoGrupo_ComparaSemAcentoENaCaixa()
    {
        var r = Interpretar(PlanilhaSintetica.Grupo("servicos"), PlanilhaSintetica.Item("1", "X", "5,00"));

        Assert.True(r.ViuGrupoDeServicos);
        Assert.Single(r.Servicos);
    }

    [Theory]
    [InlineData("70,00", 70.00)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("0,00", 0.00)]
    [InlineData("150", 150.00)]
    [InlineData("9.999.999.999,99", 9999999999.99)]
    public void PrecoBrasileiro_VirraDecimal(string texto, double esperado)
    {
        var r = Interpretar(PlanilhaSintetica.Grupo("SERVICOS"), PlanilhaSintetica.Item("1", "X", texto));

        Assert.Equal((decimal)esperado, r.Servicos[0].Dados.PrecoSugerido);
        Assert.Empty(r.Ocorrencias);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5,00")]
    [InlineData("1,005")]
    [InlineData("10.000.000.000,00")]
    [InlineData("1,2,3")]
    public void PrecoInvalido_ImportaSemPreco_ComAviso_SemEcoar(string texto)
    {
        var r = Interpretar(PlanilhaSintetica.Grupo("SERVICOS"), PlanilhaSintetica.Item("1", "X", texto));

        Assert.Null(r.Servicos[0].Dados.PrecoSugerido);
        var aviso = Assert.Single(r.Ocorrencias);
        Assert.Equal(GravidadeOcorrencia.Aviso, aviso.Gravidade);
        Assert.Equal("Preço ilegível ou fora do aceito: importado sem preço sugerido.", aviso.Motivo);
    }

    [Fact]
    public void SemPreco_ImportaSemPreco_ComAviso()
    {
        var r = Interpretar(PlanilhaSintetica.Grupo("SERVICOS"), PlanilhaSintetica.Item("1", "X", null));

        Assert.Null(r.Servicos[0].Dados.PrecoSugerido);
        Assert.Equal("Sem preço: importado sem preço sugerido.", Assert.Single(r.Ocorrencias).Motivo);
    }

    [Fact]
    public void Inativo_SIM_ImportaInativo()
    {
        var r = Interpretar(PlanilhaSintetica.Grupo("SERVICOS"),
            PlanilhaSintetica.Item("1", "A", "1,00", "SIM"), PlanilhaSintetica.Item("2", "B", "1,00", "NÃO"));

        Assert.Equal([false, true], r.Servicos.Select(s => s.Ativo).ToArray());
    }

    [Fact]
    public void DescricaoELinhasQueNaoSaoItem_SubgrupoTotalEBranco_SaoIgnoradas()
    {
        var r = Interpretar(
            PlanilhaSintetica.Grupo("SERVICOS"),
            PlanilhaSintetica.Subgrupo(),
            PlanilhaSintetica.Vazia(),
            PlanilhaSintetica.Item("1", "ITEM REAL", "1,00"),
            new Dictionary<string, string> { ["A"] = "Quantidade Cadastrada: 769" },
            new Dictionary<string, string> { ["A"] = "sem codigo no inicio", ["I"] = "5,00" });

        Assert.Single(r.Servicos);
        Assert.Equal(1, r.Lidos);
        Assert.Equal(4, r.Ignoradas); // cabeçalho + subgrupo + total + linha sem código (a linha em branco não conta)
    }

    [Fact]
    public void DescricaoELongaECodigoLongo_SaoCortadosComAviso()
    {
        var r = Interpretar(PlanilhaSintetica.Grupo("SERVICOS"), PlanilhaSintetica.Item(new string('9', 45), new string('d', 250), "1,00"));

        Assert.Equal(40, r.Servicos[0].Dados.CodigoOrigem!.Length);
        Assert.Equal(200, r.Servicos[0].Dados.Descricao.Length);
        Assert.Equal(2, r.Ocorrencias.Count);
    }

    [Fact]
    public void SemOGrupoServicos_NaoHaItens_EViuGrupoFalso()
    {
        var r = Interpretar(PlanilhaSintetica.Grupo("PECAS"), PlanilhaSintetica.Item("1", "X", "1,00"));

        Assert.True(r.EstruturaValida);
        Assert.False(r.ViuGrupoDeServicos);
        Assert.Empty(r.Servicos);
    }

    [Fact]
    public void ArquivoSemCabecalhoDeMercadorias_TemEstruturaInvalida_EItensNaoSaoLidos()
    {
        var linhas = new[]
        {
            new LinhaDaPlanilha(1, PlanilhaSintetica.CabecalhoDeClientes()),
            new LinhaDaPlanilha(2, PlanilhaSintetica.Grupo("SERVICOS")),
            new LinhaDaPlanilha(3, PlanilhaSintetica.Item("1", "X", "1,00"))
        };

        var r = InterpretadorDeServicos.Interpretar(linhas);

        Assert.False(r.EstruturaValida);
        Assert.Empty(r.Servicos);
    }

    [Fact]
    public void ItemDoGrupoAntesDoCabecalho_NaoEh_Lido()
    {
        var linhas = new[]
        {
            new LinhaDaPlanilha(1, PlanilhaSintetica.Grupo("SERVICOS")),
            new LinhaDaPlanilha(2, PlanilhaSintetica.Item("1", "X", "1,00")),
            new LinhaDaPlanilha(3, PlanilhaSintetica.CabecalhoDeMercadorias())
        };

        var r = InterpretadorDeServicos.Interpretar(linhas);

        Assert.Empty(r.Servicos);
    }
}
