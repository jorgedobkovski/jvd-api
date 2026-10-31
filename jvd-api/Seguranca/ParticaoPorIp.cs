using System.Net;
using System.Net.Sockets;

namespace jvd_api.Seguranca;

/// <summary>
/// Chave de particionamento do limite de tentativas. IPv4: o próprio endereço. IPv6: o prefixo /64
/// (quem recebe um bloco /64 controla bilhões de endereços; limitar por endereço completo seria contornável).
/// </summary>
public static class ParticaoPorIp
{
    public const string Desconhecido = "desconhecido";

    public static string Chave(IPAddress? ip)
    {
        if (ip is null)
        {
            return Desconhecido;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8); // zera os 64 bits finais, mantendo o prefixo /64
        return new IPAddress(bytes) + "/64";
    }
}
