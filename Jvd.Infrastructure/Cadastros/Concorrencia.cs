using Jvd.Application.Comum;
using Microsoft.Data.SqlClient;

namespace Jvd.Infrastructure.Cadastros;

internal static class Concorrencia
{
    /// <summary>
    /// Dois pedidos simultâneos sob isolamento serializable podem gerar deadlock no SQL Server (erro 1205): o banco
    /// derruba um deles. Isso é o mecanismo funcionando — vira conflito (409) em vez de erro 500.
    /// </summary>
    public static bool EhDeadlock(Exception ex)
    {
        for (var atual = ex; atual is not null; atual = atual.InnerException)
        {
            if (atual is SqlException { Number: 1205 }) return true;
        }

        return false;
    }

    public static ErroNegocio Conflito() =>
        new(CodigosDeErro.Conflito, "Outra alteração simultânea impediu a operação. Tente novamente.");
}
