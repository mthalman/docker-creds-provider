using System.Net;
using System.Text.Json;

namespace Valleysoft.DockerCredsProvider;

internal enum RegistryConfigFormat
{
    Containers,
    Docker,
}

internal sealed class RegistryConfigFile
{
    public RegistryConfigFile(string path, RegistryConfigFormat format)
    {
        Path = path;
        Format = format;
    }

    public string Path { get; }

    public RegistryConfigFormat Format { get; }
}

internal sealed class RegistryReference
{
    internal const string DockerHubAuthKey = "https://index.docker.io/v1/";

    private RegistryReference(string authority, string path, string containersKey)
    {
        Authority = authority;
        Path = path;
        ContainersKey = containersKey;
    }

    internal string Authority { get; }

    internal string Path { get; }

    internal string ContainersKey { get; }

    private string NormalizedContainersKey =>
        Path.Length == 0 ? Authority : $"{Authority}/{Path}";

    internal string DockerKey =>
        IsDockerHubHost(Authority) ? DockerHubAuthKey : Authority;

    internal static RegistryReference Parse(string registry)
    {
        if (registry is null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        if (!TryParse(registry, out RegistryReference? reference))
        {
            throw new ArgumentException(
                $"'{registry}' is not a valid registry hostname or HTTP(S) URL.",
                nameof(registry));
        }

        return reference!;
    }

    internal JsonProperty? FindCredentialHelper(JsonElement credHelpers, RegistryConfigFormat format)
    {
        string key = format == RegistryConfigFormat.Docker ? DockerKey : Authority;
        return FindOrdinalProperty(credHelpers, key);
    }

    internal JsonProperty? FindAuth(JsonElement auths, RegistryConfigFormat format)
        => format == RegistryConfigFormat.Containers
            ? FindContainersAuth(auths)
            : FindDockerAuth(auths);

    private JsonProperty? FindDockerAuth(JsonElement auths)
    {
        JsonProperty? exactProperty = FindExactProperty(auths, DockerKey);
        if (exactProperty is not null)
        {
            return exactProperty;
        }

        JsonProperty? httpsProperty = FindExactProperty(auths, $"https://{Authority}");
        if (httpsProperty is not null)
        {
            return httpsProperty;
        }

        JsonProperty? httpProperty = FindExactProperty(auths, $"http://{Authority}");
        if (httpProperty is not null)
        {
            return httpProperty;
        }

        return FindFirstPropertyByName(
            auths.EnumerateObject().Where(property =>
                TryParse(property.Name, out RegistryReference? configuredRegistry) &&
                configuredRegistry is not null &&
                NormalizeRegistryHost(configuredRegistry.Authority, StringComparison.OrdinalIgnoreCase)
                    .Equals(
                        NormalizeRegistryHost(Authority, StringComparison.OrdinalIgnoreCase),
                        StringComparison.OrdinalIgnoreCase)));
    }

    private JsonProperty? FindContainersAuth(JsonElement auths)
    {
        JsonProperty? exactProperty = FindOrdinalProperty(auths, ContainersKey);
        if (exactProperty is not null)
        {
            return exactProperty;
        }

        string candidate = NormalizedContainersKey;
        while (true)
        {
            JsonProperty? property = FindOrdinalProperty(auths, candidate);
            if (property is not null)
            {
                return property;
            }

            int separatorIndex = candidate.LastIndexOf('/');
            if (separatorIndex < 0)
            {
                break;
            }

            candidate = candidate.Substring(0, separatorIndex);
        }

        return FindNormalizedContainersAuth(auths);
    }

    private static JsonProperty? FindExactProperty(JsonElement properties, string key)
    {
        JsonProperty? exactProperty = FindOrdinalProperty(properties, key);
        if (exactProperty is not null)
        {
            return exactProperty;
        }

        return FindFirstPropertyByName(
            properties.EnumerateObject().Where(
                property => property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    private static JsonProperty? FindOrdinalProperty(JsonElement properties, string key)
    {
        JsonProperty? match = null;
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (property.Name.Equals(key, StringComparison.Ordinal))
            {
                match = property;
            }
        }

        return match;
    }

    private static JsonProperty? FindFirstPropertyByName(IEnumerable<JsonProperty> properties) =>
        properties
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Cast<JsonProperty?>()
            .FirstOrDefault();

    private JsonProperty? FindNormalizedContainersAuth(JsonElement auths)
    {
        string normalizedAuthority = NormalizeRegistryHost(Authority, StringComparison.Ordinal);

        return FindFirstPropertyByName(
            auths.EnumerateObject().Where(property =>
                TryParse(property.Name, out RegistryReference? configuredRegistry) &&
                configuredRegistry is not null &&
                (property.Name.IndexOf('/') < 0 ||
                 property.Name.IndexOf("://", StringComparison.Ordinal) >= 0) &&
                NormalizeRegistryHost(configuredRegistry.Authority, StringComparison.Ordinal)
                    .Equals(normalizedAuthority, StringComparison.Ordinal)));
    }

    private static bool TryParse(string registry, out RegistryReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(registry) ||
            !registry.Equals(registry.Trim(), StringComparison.Ordinal) ||
            registry.Any(char.IsControl) ||
            registry.IndexOfAny(['?', '#', '\\']) >= 0)
        {
            return false;
        }

        if (!TryGetLocation(registry, out string location))
        {
            return false;
        }

        int pathSeparator = location.IndexOf('/');
        string authority = pathSeparator < 0 ? location : location.Substring(0, pathSeparator);
        string path = pathSeparator < 0
            ? string.Empty
            : location.Substring(pathSeparator + 1).TrimEnd('/');

        if (!IsValidAuthority(authority) || !IsValidPath(path))
        {
            return false;
        }

        reference = new RegistryReference(authority, path, location);
        return true;
    }

    private static bool TryGetLocation(string registry, out string location)
    {
        location = registry;
        int schemeSeparator = registry.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            string scheme = registry.Substring(0, schemeSeparator);
            if (!scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(registry, UriKind.Absolute, out Uri? absoluteUri) ||
                absoluteUri.Host.Length == 0 ||
                absoluteUri.UserInfo.Length > 0)
            {
                return false;
            }

            location = registry.Substring(schemeSeparator + 3);
        }

        return location.IndexOf("//", StringComparison.Ordinal) < 0;
    }

    private static bool IsValidPath(string path) =>
        path.Length == 0 ||
        (path.IndexOf('@') < 0 &&
         path.IndexOf(':') < 0 &&
         path.Split('/').All(segment =>
             segment.Length > 0 &&
             !segment.Any(char.IsWhiteSpace)));

    private static bool IsValidAuthority(string authority)
    {
        if (authority.Length == 0 ||
            authority.IndexOf('@') >= 0 ||
            authority.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string host = authority;
        string? port = null;

        if (authority[0] == '[')
        {
            int bracket = authority.IndexOf(']');
            if (bracket <= 1 ||
                !IPAddress.TryParse(authority.Substring(1, bracket - 1), out IPAddress? address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
                !IsValidIPv6Address(authority.Substring(1, bracket - 1), address))
            {
                return false;
            }

            host = authority.Substring(1, bracket - 1);
            if (bracket + 1 < authority.Length)
            {
                if (authority[bracket + 1] != ':')
                {
                    return false;
                }

                port = authority.Substring(bracket + 2);
            }
        }
        else
        {
            int firstColon = authority.IndexOf(':');
            int lastColon = authority.LastIndexOf(':');
            if (firstColon >= 0 && firstColon == lastColon)
            {
                host = authority.Substring(0, firstColon);
                port = authority.Substring(firstColon + 1);
            }
            else if (firstColon >= 0 && !IPAddress.TryParse(authority, out _))
            {
                return false;
            }
        }

        if (!IsValidRegistryHost(host))
        {
            return false;
        }

        return IsValidPort(port);
    }

    private static bool IsValidPort(string? port) =>
        port is null ||
        (port.Length > 0 &&
         port.All(character => character is >= '0' and <= '9') &&
         int.TryParse(port, out int portNumber) &&
         portNumber is > 0 and <= 65535);

    private static bool IsValidRegistryHost(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
                IsValidIPv6Address(host, address);
        }

        return host.Length > 0 &&
            host.Split('.').All(label =>
                label.Length > 0 &&
                IsAsciiLetterOrDigit(label[0]) &&
                IsAsciiLetterOrDigit(label[label.Length - 1]) &&
                label.All(character => IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static bool IsValidIPv6Address(string host, IPAddress address) =>
        host.IndexOf('%') < 0 &&
        host.IndexOf('.') < 0 &&
        !address.IsIPv4MappedToIPv6;

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9';

    private static string NormalizeRegistryHost(string authority, StringComparison comparison) =>
        IsDockerHubAlias(authority, comparison)
            ? "index.docker.io"
            : authority;

    private static bool IsDockerHubAlias(string authority, StringComparison comparison) =>
        authority.Equals("docker.io", comparison) ||
        authority.Equals("registry-1.docker.io", comparison);

    private static bool IsDockerHubHost(string authority) =>
        authority.Equals("index.docker.io", StringComparison.Ordinal) ||
        IsDockerHubAlias(authority, StringComparison.Ordinal);
}
