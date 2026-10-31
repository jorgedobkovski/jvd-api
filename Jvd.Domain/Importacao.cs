namespace Jvd.Domain;

public enum TipoImportacao
{
    Clientes = 1,
    Servicos = 2
}

public enum GravidadeOcorrencia
{
    /// <summary>O registro foi importado, mas algo precisa de revisão.</summary>
    Aviso = 1,

    /// <summary>O registro NÃO foi importado.</summary>
    Erro = 2
}

/// <summary>
/// Registro de uma importação real (Specs/IMPORTACAO.md): quem, quando, qual arquivo (hash) e os totais. O hash,
/// único por tipo, barra a reimportação acidental do mesmo arquivo.
/// </summary>
public sealed class Importacao
{
    public Guid Id { get; set; }
    public TipoImportacao Tipo { get; set; }

    /// <summary>Só o nome do arquivo, sem pasta.</summary>
    public string NomeArquivo { get; set; } = "";

    /// <summary>SHA-256 do conteúdo, em hexadecimal minúsculo (64 caracteres).</summary>
    public string HashArquivo { get; set; } = "";

    public Guid UsuarioId { get; set; }
    public DateTime ExecutadaEm { get; set; }
    public int TotalLidos { get; set; }
    public int TotalImportados { get; set; }
    public int TotalRejeitados { get; set; }
    public int TotalAvisos { get; set; }
    public int TotalIgnoradas { get; set; }
}

/// <summary>Ocorrência de uma linha. Guarda só o número da linha e um motivo FIXO: nunca o conteúdo (dados pessoais — LGPD).</summary>
public sealed class ImportacaoErro
{
    public Guid Id { get; set; }
    public Guid ImportacaoId { get; set; }
    public int Linha { get; set; }
    public GravidadeOcorrencia Gravidade { get; set; }
    public string Motivo { get; set; } = "";
}
