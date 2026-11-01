using System.Globalization;
using System.Text.Json;
using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace Jvd.Infrastructure.Relatorios;

/// <summary>
/// Exportação dos relatórios EMITIDOS para .xlsx (RF-033). Sem CPF/CNPJ (decisão do Owner). Cliente e equipamento vêm
/// dos snapshots da emissão. Limita o tamanho para a resposta caber na memória; acima disso pede para refinar o filtro.
/// </summary>
public sealed class ExportadorDeRelatorios(JvdDbContext db) : IExportadorDeRelatorios
{
    public const int LimiteDeLinhas = 20_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Cabecalho =
        ["Nº", "Data do serviço", "Tipo de serviço", "Executor", "Cliente", "Equipamento", "Total (R$)", "Desconto (R$)", "Total geral (R$)", "Situação"];

    private sealed class Linha
    {
        public int? Numero { get; init; }
        public DateOnly DataServico { get; init; }
        public TipoServico TipoServico { get; init; }
        public decimal Total { get; init; }
        public decimal Desconto { get; init; }
        public decimal TotalGeral { get; init; }
        public int Revisao { get; init; }
        public string? ClienteSnapshot { get; init; }
        public string? EquipamentoSnapshot { get; init; }
        public string ClienteNomeAtual { get; init; } = "";
        public string EquipamentoDescricaoAtual { get; init; } = "";
        public string TecnicoNome { get; init; } = "";
    }

    public async Task<Resultado<ArquivoExportado>> ExportarAsync(FiltroDeExportacao filtro, CancellationToken ct = default)
    {
        if (filtro.De is { } inicio && filtro.Ate is { } fim && fim < inicio)
        {
            return Resultado<ArquivoExportado>.Falha(new ErroNegocio(CodigosDeErro.Validacao, "A data final não pode ser anterior à inicial."));
        }

        var consulta = db.Relatorios.AsNoTracking().Where(r => r.Status == StatusRelatorio.Emitido);
        if (filtro.De is { } de) consulta = consulta.Where(r => r.DataServico >= de);
        if (filtro.Ate is { } ate) consulta = consulta.Where(r => r.DataServico <= ate);
        if (filtro.ClienteId is { } cliente) consulta = consulta.Where(r => r.ClienteId == cliente);
        if (filtro.EquipamentoId is { } equipamento) consulta = consulta.Where(r => r.EquipamentoId == equipamento);
        if (filtro.TecnicoId is { } tecnico) consulta = consulta.Where(r => r.TecnicoId == tecnico);
        if (filtro.Tipo is { } tipo) consulta = consulta.Where(r => r.TipoServico == tipo);

        var total = await consulta.CountAsync(ct);
        if (total > LimiteDeLinhas)
        {
            return Resultado<ArquivoExportado>.Falha(new ErroNegocio(
                CodigosDeErro.Validacao, $"A exportação passa de {LimiteDeLinhas:N0} relatórios. Refine o período ou os filtros e tente de novo."));
        }

        var linhas = await consulta
            .OrderBy(r => r.Numero)
            .Select(r => new Linha
            {
                Numero = r.Numero, DataServico = r.DataServico, TipoServico = r.TipoServico, Total = r.Total, Desconto = r.Desconto,
                TotalGeral = r.TotalGeral, Revisao = r.Revisao, ClienteSnapshot = r.ClienteSnapshot, EquipamentoSnapshot = r.EquipamentoSnapshot,
                ClienteNomeAtual = db.Clientes.Where(c => c.Id == r.ClienteId).Select(c => c.NomeRazaoSocial).FirstOrDefault() ?? "",
                EquipamentoDescricaoAtual = db.Equipamentos.Where(e => e.Id == r.EquipamentoId).Select(e => e.Descricao).FirstOrDefault() ?? "",
                TecnicoNome = db.Users.Where(u => u.Id == r.TecnicoId).Select(u => u.Nome).FirstOrDefault() ?? ""
            })
            .ToListAsync(ct);

        var conteudo = EscritorXlsx.Escrever("Relatórios emitidos", Cabecalho, linhas.Select(Celulas));
        return Resultado<ArquivoExportado>.Ok(new ArquivoExportado(conteudo, NomeDoArquivo(filtro), linhas.Count));
    }

    private static IReadOnlyList<object?> Celulas(Linha l) =>
    [
        l.Numero, l.DataServico, RotuloDoTipo(l.TipoServico), l.TecnicoNome,
        NomeDoCliente(l), DescricaoDoEquipamento(l), l.Total, l.Desconto, l.TotalGeral,
        l.Revisao > 0 ? $"Retificado (revisão {l.Revisao})" : "Emitido"
    ];

    /// <summary>O nome do cliente no momento da emissão (snapshot); só cai para o cadastro atual se o snapshot faltar.</summary>
    private static string NomeDoCliente(Linha l) => Ler<SnapshotDoCliente>(l.ClienteSnapshot)?.Nome ?? l.ClienteNomeAtual;

    private static string DescricaoDoEquipamento(Linha l) => Ler<SnapshotDoEquipamento>(l.EquipamentoSnapshot)?.Descricao ?? l.EquipamentoDescricaoAtual;

    private static T? Ler<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RotuloDoTipo(TipoServico tipo) => tipo switch
    {
        TipoServico.Corretiva => "Manutenção corretiva",
        TipoServico.Preventiva => "Manutenção preventiva",
        TipoServico.Instalacao => "Instalação",
        _ => "Inspeção"
    };

    /// <summary>Nome só com dígitos, letras e hífen: nunca dado do usuário.</summary>
    private static string NomeDoArquivo(FiltroDeExportacao filtro)
    {
        string Dia(DateOnly? d) => d is { } v ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        var periodo = filtro.De is null && filtro.Ate is null ? "" : $"-{Dia(filtro.De)}-a-{Dia(filtro.Ate)}".Replace("--", "-").TrimEnd('-');
        return $"relatorios-emitidos{periodo}.xlsx";
    }
}
