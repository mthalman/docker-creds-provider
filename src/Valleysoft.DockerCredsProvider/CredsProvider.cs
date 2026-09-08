using System.Text.Json;

namespace Valleysoft.DockerCredsProvider;

/// <summary>
/// Resolves registry credentials from Docker-compatible configuration files.
/// </summary>
public static class CredsProvider
{
    private static readonly IEnvironment _defaultEnvironment = new EnvironmentWrapper();
    private static readonly IProcessService _defaultProcessService = new ProcessService();
    private static readonly IFileSystem _defaultFileSystem = new FileSystem();

    /// <summary>
    /// Gets the credentials configured for a registry.
    /// </summary>
    /// <param name="registry">The registry hostname or URL to resolve.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result
    /// contains the configured username and credential.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="registry"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="registry"/> is empty, whitespace-only, or is not a
    /// valid registry hostname or HTTP(S) URL.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// No Docker-compatible configuration file exists in a configured location.
    /// </exception>
    /// <exception cref="CredsNotFoundException">
    /// No matching credentials are configured for <paramref name="registry"/>,
    /// or its credential helper reports a failure.
    /// </exception>
    /// <exception cref="JsonException">
    /// A configuration file contains invalid JSON or is missing a required
    /// credential value.
    /// </exception>
    /// <exception cref="FormatException">
    /// An inline credential value is not valid Base64.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A configuration value has an unexpected JSON type, or a native
    /// credential helper cannot be located, executed, or returns an invalid
    /// response.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// A native credential helper does not complete within its timeout.
    /// </exception>
    public static Task<DockerCredentials> GetCredentialsAsync(string registry) =>
        GetCredentialsAsync(registry, CancellationToken.None);

    /// <summary>
    /// Gets the credentials configured for a registry.
    /// </summary>
    /// <param name="registry">The registry hostname or URL to resolve.</param>
    /// <param name="cancellationToken">
    /// A token that can cancel credential retrieval.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result
    /// contains the configured username and credential.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="registry"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="registry"/> is empty, whitespace-only, or is not a
    /// valid registry hostname or HTTP(S) URL.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// No Docker-compatible configuration file exists in a configured location.
    /// </exception>
    /// <exception cref="CredsNotFoundException">
    /// No matching credentials are configured for <paramref name="registry"/>,
    /// or its credential helper reports a failure.
    /// </exception>
    /// <exception cref="JsonException">
    /// A configuration file contains invalid JSON or is missing a required
    /// credential value.
    /// </exception>
    /// <exception cref="FormatException">
    /// An inline credential value is not valid Base64.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A configuration value has an unexpected JSON type, or a native
    /// credential helper cannot be located, executed, or returns an invalid
    /// response.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// A native credential helper does not complete within its timeout.
    /// </exception>
    public static Task<DockerCredentials> GetCredentialsAsync(string registry, CancellationToken cancellationToken) =>
        GetCredentialsAsync(registry, _defaultFileSystem, _defaultProcessService, _defaultEnvironment, cancellationToken);

    internal static async Task<DockerCredentials> GetCredentialsAsync(
        string registry,
        IFileSystem fileSystem,
        IProcessService processService,
        IEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        RegistryReference registryReference = RegistryReference.Parse(registry);

        cancellationToken.ThrowIfCancellationRequested();

        (ICredStore credStore, string serverAddress) = await GetCredStoreAsync(
            registryReference,
            registry,
            fileSystem,
            processService,
            environment,
            cancellationToken);
        return await credStore.GetCredentialsAsync(serverAddress, cancellationToken);
    }

    /// <summary>
    /// Returns a set of candidate file paths for the config file that should be checked in the order listed (i.e. they are listed in priority order).
    /// </summary>
    internal static string[] GetConfigFilePaths(IEnvironment env)
    {
        return GetConfigFiles(env)
            .Select(configFile => configFile.Path)
            .ToArray();
    }

    private static RegistryConfigFile[] GetConfigFiles(IEnvironment env)
    {
        if (env.GetEnvironmentVariable("REGISTRY_AUTH_FILE") is { Length: > 0 } configFile)
        {
            return [new RegistryConfigFile(configFile, RegistryConfigFormat.Containers)];
        }

        List<RegistryConfigFile> configFiles = [];

        if (env.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdgRuntimeDir)
        {
            configFiles.Add(new RegistryConfigFile(
                Path.Combine(xdgRuntimeDir, "containers", "auth.json"),
                RegistryConfigFormat.Containers));
        }

        string? xdgConfigDir = env.GetEnvironmentVariable("XDG_CONFIG_DIR");
        if (string.IsNullOrEmpty(xdgConfigDir))
        {
            xdgConfigDir = Path.Combine(env.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        configFiles.Add(new RegistryConfigFile(
            Path.Combine(xdgConfigDir, "containers", "auth.json"),
            RegistryConfigFormat.Containers));

        string? dockerConfigDir = env.GetEnvironmentVariable("DOCKER_CONFIG");
        if (string.IsNullOrEmpty(dockerConfigDir))
        {
            dockerConfigDir = Path.Combine(env.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        }
        configFiles.Add(new RegistryConfigFile(
            Path.Combine(dockerConfigDir, "config.json"),
            RegistryConfigFormat.Docker));

        return [.. configFiles];
    }

    private static async Task<(ICredStore CredStore, string ServerAddress)> GetCredStoreAsync(
        RegistryReference registryReference,
        string requestedRegistry,
        IFileSystem fileSystem,
        IProcessService processService,
        IEnvironment environment,
        CancellationToken cancellationToken)
    {
        RegistryConfigFile[] configFiles = GetConfigFiles(environment);

        bool configFileFound = false;
        foreach (RegistryConfigFile configFile in configFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!fileSystem.FileExists(configFile.Path))
            {
                continue;
            }

            configFileFound = true;

            using Stream openStream = fileSystem.FileOpenRead(configFile.Path);
            using JsonDocument configDoc = await JsonDocument.ParseAsync(openStream, cancellationToken: cancellationToken);

            if (configDoc.RootElement.TryGetProperty("credHelpers", out JsonElement credHelpersElement))
            {
                JsonProperty? credHelperProperty = registryReference.FindCredentialHelper(
                    credHelpersElement,
                    configFile.Format);
                if (credHelperProperty is JsonProperty property)
                {
                    string? credHelperName = property.Value.GetString();
                    return credHelperName is null
                        ? throw new JsonException(
                            $"Name of the credHelper for host '{property.Name}' was not set in Docker config {configFile.Path}.")
                        : (new NativeStore(credHelperName, processService, fileSystem, environment), property.Name);
                }
            }

            if (configFile.Format == RegistryConfigFormat.Docker &&
                configDoc.RootElement.TryGetProperty("credsStore", out JsonElement credsStoreElement))
            {
                string? credHelperName = credsStoreElement.GetString();
                return credHelperName is null
                    ? throw new JsonException($"Name of the credsStore was not set in Docker config {configFile.Path}.")
                    : (
                        new NativeStore(credHelperName, processService, fileSystem, environment),
                        registryReference.DockerKey);
            }

            if (configDoc.RootElement.TryGetProperty("auths", out JsonElement authsElement))
            {
                JsonProperty? property = registryReference.FindAuth(authsElement, configFile.Format);
                if (property is null)
                {
                    continue;
                }

                return (new EncodedStore(property.Value, configFile.Path), property.Value.Name);
            }
        }

        if (!configFileFound)
        {
            throw new FileNotFoundException($"Docker config file doesn't exist.");
        }

        throw new CredsNotFoundException($"No matching auth specified for registry '{requestedRegistry}' in Docker config.");
    }
}
