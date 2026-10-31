namespace Jvd.Domain;

/// <summary>
/// Perfil do usuário no sistema. É a fonte única de permissão (ver Specs/AUTENTICACAO_PERMISSOES.md).
/// O gestor herda tudo o que o técnico faz.
/// </summary>
public enum PerfilUsuario
{
    Tecnico = 1,
    Gestor = 2
}
