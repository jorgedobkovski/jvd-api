using Jvd.Domain;
using Microsoft.AspNetCore.Authorization;

namespace jvd_api.Seguranca;

/// <summary>
/// Políticas de autorização por perfil (Specs/AUTENTICACAO_PERMISSOES.md).
/// O gestor herda tudo o que o técnico faz.
/// </summary>
public static class Politicas
{
    public const string Tecnico = "Tecnico";
    public const string Gestor = "Gestor";

    public static void Configurar(AuthorizationOptions opcoes)
    {
        opcoes.AddPolicy(Tecnico, p => p.RequireAuthenticatedUser()
            .RequireRole(nameof(PerfilUsuario.Tecnico), nameof(PerfilUsuario.Gestor)));

        opcoes.AddPolicy(Gestor, p => p.RequireAuthenticatedUser()
            .RequireRole(nameof(PerfilUsuario.Gestor)));

        // Seguro por padrão: todo endpoint exige usuário autenticado, a menos que tenha [AllowAnonymous].
        opcoes.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}
