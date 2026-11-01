using Jvd.Application.Clientes;
using Jvd.Application.Emissor;
using Jvd.Application.Equipamentos;
using Microsoft.Extensions.DependencyInjection;

namespace Jvd.Tests.Suporte;

/// <summary>Monta cadastros SINTÉTICOS (cliente, equipamento, emissor) pelos serviços reais, para os testes do relatório.</summary>
public static class CadastrosDeTeste
{
    public const string CnpjDoEmissor = "11222333000181";

    public static async Task<ClienteResumo> NovoClienteAsync(IServiceProvider sp, Guid ator, bool comDocumento = true)
    {
        await using var escopo = sp.CreateAsyncScope();
        var servico = escopo.ServiceProvider.GetRequiredService<IClienteService>();
        var dados = new DadosDoCliente("Cliente Sintético " + Guid.NewGuid().ToString("N")[..8], "Fantasia", comDocumento ? DadosDeTeste.NovoCpf() : null,
            "123", "RUA SINTETICA", "10", "CENTRO", null, "CIDADE TESTE", "RO", "69999999999", null);
        var r = await servico.CriarAsync(dados, ator);
        if (!r.Sucesso) throw new InvalidOperationException("Falha ao montar o cliente do cenário.");
        return r.Valor!.Cliente;
    }

    public static async Task<EquipamentoResumo> NovoEquipamentoAsync(IServiceProvider sp, Guid ator, Guid clienteId)
    {
        await using var escopo = sp.CreateAsyncScope();
        var servico = escopo.ServiceProvider.GetRequiredService<IEquipamentoService>();
        var r = await servico.CriarAsync(new DadosDoEquipamento(clienteId, "Balança sintética", "SER-" + Guid.NewGuid().ToString("N")[..6], "INM-1", null), ator);
        if (!r.Sucesso) throw new InvalidOperationException("Falha ao montar o equipamento do cenário.");
        return r.Valor!.Equipamento;
    }

    /// <summary>Configura o emissor se ainda não existir (é um registro único por banco).</summary>
    public static async Task GarantirEmissorAsync(IServiceProvider sp, Guid ator, bool comResponsavelTecnico = true)
    {
        await using var escopo = sp.CreateAsyncScope();
        var servico = escopo.ServiceProvider.GetRequiredService<IEmissorService>();
        if ((await servico.ObterAsync()).Sucesso) return;

        var dados = new DadosDoEmissor("Empresa Sintética Ltda", "Empresa", CnpjDoEmissor, "999", "RUA DA EMPRESA", "1", "CENTRO", null,
            "PORTO VELHO", "RO", "(69) 0000-0000", "IPEM-SINTETICO", comResponsavelTecnico ? "Responsável Sintético" : null,
            comResponsavelTecnico ? DadosDeTeste.NovoCpf() : null);
        var r = await servico.SalvarAsync(dados, null, ator);
        if (!r.Sucesso) throw new InvalidOperationException("Falha ao montar o emissor do cenário.");
    }
}
