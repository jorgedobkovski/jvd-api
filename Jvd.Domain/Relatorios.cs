namespace Jvd.Domain;

public enum StatusRelatorio
{
    Rascunho = 1,
    Emitido = 2,
    Cancelado = 3
}

public enum TipoServico
{
    Corretiva = 1,
    Preventiva = 2,
    Instalacao = 3,
    Inspecao = 4
}

public enum TipoEventoRelatorio
{
    Criado = 1,
    Emitido = 2,
    Retificado = 3,
    Cancelado = 4
}

/// <summary>
/// Relatório de Serviço (Specs/RELATORIO_SERVICO.md). Um relatório = um equipamento. O número só existe depois da
/// emissão e nunca muda nem se repete. Depois de emitido só muda por retificação (com histórico). Os snapshots são
/// JSON com dados pessoais do cliente: mesmo acesso restrito do relatório, nunca em log.
/// </summary>
public sealed class Relatorio
{
    public Guid Id { get; set; }

    /// <summary>Nulo enquanto rascunho; sequencial sem buracos, atribuído na emissão.</summary>
    public int? Numero { get; set; }

    public StatusRelatorio Status { get; set; }
    public TipoServico TipoServico { get; set; }
    public DateOnly DataServico { get; set; }

    public Guid ClienteId { get; set; }
    public Guid EquipamentoId { get; set; }

    /// <summary>O técnico (ou gestor) que executou o serviço.</summary>
    public Guid TecnicoId { get; set; }

    public Guid CriadoPor { get; set; }

    public string ServicoExecutado { get; set; } = "";
    public string? Observacoes { get; set; }

    public DateOnly? VerificacaoAnteriorData { get; set; }
    public string? VerificacaoAnteriorCertificado { get; set; }
    public string? NumeroSeloReparador { get; set; }

    public int GarantiaDias { get; set; }
    public decimal Total { get; set; }
    public decimal Desconto { get; set; }

    /// <summary>Sempre <c>Total − Desconto</c>, calculado e gravado.</summary>
    public decimal TotalGeral { get; set; }

    /// <summary>Só rastreia a sugestão do catálogo usada; o relatório guarda o texto e o valor finais.</summary>
    public Guid? CatalogoServicoId { get; set; }

    /// <summary>0 = original; incrementa a cada retificação (4B).</summary>
    public int Revisao { get; set; }

    public string? ClienteSnapshot { get; set; }
    public string? EquipamentoSnapshot { get; set; }
    public string? EmissorSnapshot { get; set; }

    public DateTime? EmitidoEm { get; set; }
    public Guid? EmitidoPor { get; set; }

    public DateTime? CanceladoEm { get; set; }
    public Guid? CanceladoPor { get; set; }
    public string? MotivoCancelamento { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }
    public byte[] Versao { get; set; } = [];
}

/// <summary>Trilha do relatório. O detalhe é texto curto e <b>sem dados pessoais</b>.</summary>
public sealed class RelatorioEvento
{
    public Guid Id { get; set; }
    public Guid RelatorioId { get; set; }
    public TipoEventoRelatorio Tipo { get; set; }
    public Guid UsuarioId { get; set; }
    public DateTime OcorridoEm { get; set; }
    public string? Detalhe { get; set; }
}

/// <summary>
/// Contador da numeração. A emissão incrementa a linha na MESMA transação que grava o relatório: se a emissão
/// falhar, o número volta (sem buracos); emissões simultâneas esperam umas pelas outras.
/// </summary>
public sealed class ContadorDeNumeracao
{
    public const string ChaveDoRelatorio = "relatorio";

    public string Chave { get; set; } = "";
    public int Ultimo { get; set; }
}

/// <summary>
/// Histórico de retificações de um relatório emitido: quem, quando, por quê e o que mudou. Os JSON guardam só os
/// campos alterados (valores antes e depois) e podem conter dados pessoais do cliente: mesmo acesso restrito do
/// relatório, nunca em log. Único por (relatório, revisão).
/// </summary>
public sealed class RelatorioRevisao
{
    public Guid Id { get; set; }
    public Guid RelatorioId { get; set; }

    /// <summary>1, 2, … (a original é a revisão 0 e não gera linha aqui).</summary>
    public int Revisao { get; set; }

    public DateTime RetificadoEm { get; set; }
    public Guid RetificadoPor { get; set; }
    public string Motivo { get; set; } = "";
    public string DadosAnteriores { get; set; } = "{}";
    public string DadosNovos { get; set; } = "{}";
}
