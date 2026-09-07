namespace Valleysoft.DockerCredsProvider;

/// <summary>
/// Represents credentials configured for a Docker registry.
/// </summary>
public class DockerCredentials
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DockerCredentials"/> class.
    /// </summary>
    /// <param name="username">The registry username.</param>
    /// <param name="password">
    /// The registry password, if one is configured.
    /// </param>
    /// <param name="identityToken">
    /// The registry identity token, if one is configured.
    /// </param>
    public DockerCredentials(string username, string? password = null, string? identityToken = null)
    {
        Username = username;
        Password = password;
        IdentityToken = identityToken;
    }

    /// <summary>
    /// Gets the registry username.
    /// </summary>
    public string Username { get; }

    /// <summary>
    /// Gets the registry password, if one is configured.
    /// </summary>
    public string? Password { get; }

    /// <summary>
    /// Gets the registry identity token, if one is configured.
    /// </summary>
    public string? IdentityToken { get; }
}
