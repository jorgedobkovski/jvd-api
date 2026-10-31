using System.Net;
using jvd_api.Seguranca;

namespace Jvd.Tests.Seguranca;

public class ParticaoPorIpTests
{
    private static string Chave(string ip) => ParticaoPorIp.Chave(IPAddress.Parse(ip));

    [Fact]
    public void SemEndereco_UsaChaveDesconhecido() =>
        Assert.Equal(ParticaoPorIp.Desconhecido, ParticaoPorIp.Chave(null));

    [Fact]
    public void Ipv4_UsaOProprioEndereco()
    {
        Assert.Equal("203.0.113.7", Chave("203.0.113.7"));
        Assert.NotEqual(Chave("203.0.113.7"), Chave("203.0.113.8"));
    }

    [Fact]
    public void Ipv4MapeadoEmIpv6_EquivaleAoIpv4()
    {
        Assert.Equal(Chave("203.0.113.7"), Chave("::ffff:203.0.113.7"));
    }

    [Fact]
    public void Ipv6_DoMesmoPrefixo64_CaiNaMesmaParticao()
    {
        // quem tem um /64 não pode multiplicar o limite trocando os 64 bits finais
        Assert.Equal(Chave("2001:db8:1:2:aaaa:bbbb:cccc:dddd"), Chave("2001:db8:1:2:1111:2222:3333:4444"));
        Assert.EndsWith("/64", Chave("2001:db8:1:2::1"));
    }

    [Fact]
    public void Ipv6_DePrefixosDiferentes_FicamEmParticoesDiferentes() =>
        Assert.NotEqual(Chave("2001:db8:1:2::1"), Chave("2001:db8:1:3::1"));
}
