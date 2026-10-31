using Jvd.Domain;
using Microsoft.AspNetCore.Identity;

namespace Jvd.Infrastructure.Identity;

/// <summary>
/// Usuário do sistema (técnico ou gestor). Estende o IdentityUser: a tabela de usuários do Identity
/// é a única tabela de usuários (Database/MODELO_DADOS.md). <c>UserName</c> é sempre igual ao e-mail.
/// </summary>
public sealed class Usuario : IdentityUser<Guid>
{
    public string Nome { get; set; } = "";
    public PerfilUsuario Perfil { get; set; }
    public string? Cpf { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }
}
