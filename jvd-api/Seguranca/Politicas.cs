using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Microsoft.AspNetCore.Authorization;

namespace jvd_api.Seguranca;

/// <summary>
/// Políticas de autorização por perfil (Specs/AUTENTICACAO_PERMISSOES.md).
/// O gestor herda tudo o que o técnico faz. Quem está com senha provisória (claim <c>trocar_senha</c>)
/// só passa pela política <see cref="Autenticado"/> — ou seja, só consegue trocar a senha (ADR-006).
/// </summary>
public static class Politicas
{
    public const string Tecnico = "Tecnico";
    public const string Gestor = "Gestor";

    /// <summary>Qualquer usuário autenticado, MESMO com senha provisória (troca de senha e /me).</summary>
    public const string Autenticado = "Autenticado";

    public static void Configurar(AuthorizationOptions opcoes)
    {
        opcoes.AddPolicy(Autenticado, p => p.RequireAuthenticatedUser());

        opcoes.AddPolicy(Tecnico, p => p.RequireAuthenticatedUser()
            .RequireRole(nameof(PerfilUsuario.Tecnico), nameof(PerfilUsuario.Gestor))
            .RequireAssertion(SemTrocaDeSenhaPendente));

        opcoes.AddPolicy(Gestor, p => p.RequireAuthenticatedUser()
            .RequireRole(nameof(PerfilUsuario.Gestor))
            .RequireAssertion(SemTrocaDeSenhaPendente));

        // Seguro por padrão: todo endpoint exige usuário autenticado e sem troca de senha pendente,
        // a menos que tenha [AllowAnonymous] ou a política Autenticado.
        // - FallbackPolicy: endpoint SEM nenhum atributo de autorização.
        // - DefaultPolicy:  endpoint com [Authorize] puro (sem nome de política). Sem isto, um [Authorize] simples
        //   num controller novo deixaria passar quem está com senha provisória (auditoria 2A, P2-1).
        var padraoSeguro = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(SemTrocaDeSenhaPendente)
            .Build();

        opcoes.DefaultPolicy = padraoSeguro;
        opcoes.FallbackPolicy = padraoSeguro;
    }

    private static bool SemTrocaDeSenhaPendente(AuthorizationHandlerContext contexto) =>
        !contexto.User.HasClaim(NomesDeClaims.TrocarSenha, "true");
}
