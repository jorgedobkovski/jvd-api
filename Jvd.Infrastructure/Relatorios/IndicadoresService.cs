using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace Jvd.Infrastructure.Relatorios;

/// <summary>
/// Indicadores gerenciais (RF-034). Só relatórios <c>Emitido</c> (cancelados e rascunhos nunca entram), no período da
/// DATA DO SERVIÇO. Somas feitas no banco; nomes buscados à parte só para quem aparece nos rankings.
/// </summary>
public sealed class IndicadoresService(JvdDbContext db) : IIndicadoresService
{
    private const int QuantidadeDeMaioresClientes = 10;

    public async Task<Resultado<Indicadores>> ObterAsync(DateOnly? de, DateOnly? ate, CancellationToken ct = default)
    {
        if (de is { } inicio && ate is { } fim && fim < inicio)
        {
            return Resultado<Indicadores>.Falha(new ErroNegocio(CodigosDeErro.Validacao, "A data final não pode ser anterior à inicial."));
        }

        var consulta = db.Relatorios.AsNoTracking().Where(r => r.Status == StatusRelatorio.Emitido);
        if (de is { } d) consulta = consulta.Where(r => r.DataServico >= d);
        if (ate is { } a) consulta = consulta.Where(r => r.DataServico <= a);

        var quantidade = await consulta.CountAsync(ct);
        var total = await consulta.SumAsync(r => r.Total, ct);
        var descontos = await consulta.SumAsync(r => r.Desconto, ct);
        var totalGeral = await consulta.SumAsync(r => r.TotalGeral, ct);
        var ticket = quantidade == 0 ? 0m : Math.Round(totalGeral / quantidade, 2, MidpointRounding.AwayFromZero);

        var porExecutor = await consulta
            .GroupBy(r => r.TecnicoId)
            .Select(g => new { TecnicoId = g.Key, Quantidade = g.Count(), TotalGeral = g.Sum(r => r.TotalGeral) })
            .OrderByDescending(x => x.TotalGeral).ThenByDescending(x => x.Quantidade).ThenBy(x => x.TecnicoId)
            .ToListAsync(ct);

        var porTipo = await consulta
            .GroupBy(r => r.TipoServico)
            .Select(g => new { Tipo = g.Key, Quantidade = g.Count(), TotalGeral = g.Sum(r => r.TotalGeral) })
            .OrderBy(x => x.Tipo)
            .ToListAsync(ct);

        var maioresClientes = await consulta
            .GroupBy(r => r.ClienteId)
            .Select(g => new { ClienteId = g.Key, Quantidade = g.Count(), TotalGeral = g.Sum(r => r.TotalGeral) })
            .OrderByDescending(x => x.TotalGeral).ThenByDescending(x => x.Quantidade).ThenBy(x => x.ClienteId)
            .Take(QuantidadeDeMaioresClientes)
            .ToListAsync(ct);

        var idsDeExecutores = porExecutor.Select(x => x.TecnicoId).ToList();
        var nomesDeExecutores = await db.Users.AsNoTracking()
            .Where(u => idsDeExecutores.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Nome, ct);

        var idsDeClientes = maioresClientes.Select(x => x.ClienteId).ToList();
        var nomesDeClientes = await db.Clientes.AsNoTracking()
            .Where(c => idsDeClientes.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.NomeRazaoSocial, ct);

        return Resultado<Indicadores>.Ok(new Indicadores(
            de, ate,
            new TotaisDoPeriodo(quantidade, total, descontos, totalGeral, ticket),
            porExecutor.Select(x => new IndicadorPorExecutor(x.TecnicoId, nomesDeExecutores.GetValueOrDefault(x.TecnicoId, ""), x.Quantidade, x.TotalGeral)).ToList(),
            porTipo.Select(x => new IndicadorPorTipo(x.Tipo, x.Quantidade, x.TotalGeral)).ToList(),
            maioresClientes.Select(x => new IndicadorPorCliente(x.ClienteId, nomesDeClientes.GetValueOrDefault(x.ClienteId, ""), x.Quantidade, x.TotalGeral)).ToList()));
    }
}
