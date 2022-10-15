using System.Text.Json;

namespace Valleysoft.DockerCredsProvider;

public static class CredsProvider
{
    public static Task<DockerCredentials> GetCredentialsAsync(string registry) =>
        GetCredentialsAsync(registry, new FileSystem(), new ProcessService());

    internal static async Task<DockerCredentials> GetCredentialsAsync(string registry, IFileSystem fileSystem, IProcessService processService)
    {
        if (registry is null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        ICredStore credStore = await GetCredStoreAsync(registry, fileSystem, processService);
        return await credStore.GetCredentialsAsync(registry);
    }

    private static async Task<ICredStore> GetCredStoreAsync(string registry, IFileSystem fileSystem, IProcessService processService)
    {
        string dockerConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".docker",
            "config.json");

        if (!fileSystem.FileExists(dockerConfigPath))
        {
            throw new FileNotFoundException($"Docker config '{dockerConfigPath}' doesn't exist.");
        }

        using Stream openStream = fileSystem.FileOpenRead(dockerConfigPath);
        using JsonDocument configDoc = await JsonDocument.ParseAsync(openStream);

        if (configDoc.RootElement.TryGetProperty("credHelpers", out JsonElement credHelpersElement) &&
            credHelpersElement.TryGetProperty(registry, out JsonElement credHelperElement))
        {
            string? credHelperName = credHelperElement.GetString();
            if (credHelperName is null)
            {
                throw new JsonException($"Name of the credHelper for host '{registry}' was not set in Docker config {dockerConfigPath}.");
            }

            return new NativeStore(credHelperName, processService);
        }

        if (configDoc.RootElement.TryGetProperty("credsStore", out JsonElement credsStoreElement))
        {
            string? credHelperName = credsStoreElement.GetString();
            if (credHelperName is null)
            {
                throw new JsonException($"Name of the credsStore was not set in Docker config {dockerConfigPath}.");
            }

            return new NativeStore(credHelperName, processService);
        }

        if (configDoc.RootElement.TryGetProperty("auths", out JsonElement authsElement))
        {
            JsonProperty property = authsElement.EnumerateObject().FirstOrDefault(prop => prop.Name == registry);

            if (property.Equals(default(JsonProperty)))
            {
                throw new CredsNotFoundException($"No matching auth specified for registry '{registry}' in Docker config '{dockerConfigPath}'.");
            }

            return new EncodedStore(property, dockerConfigPath);
        }

        throw new JsonException($"Unable to find credential information in Docker config '{dockerConfigPath}'.");
    }
}
