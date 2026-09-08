using System.Text;
using Moq;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class RegistryMatchingTests
{
    [Theory]
    [InlineData("registry.example.com", "registry.example.com")]
    [InlineData("registry.example.com/team/image", "registry.example.com")]
    [InlineData("https://registry.example.com/v2/", "registry.example.com")]
    [InlineData("registry.example.com:5000/team", "registry.example.com:5000")]
    [InlineData("https://[::1]:5000/team", "[::1]:5000")]
    [InlineData("registry.example.com", "https://registry.example.com")]
    [InlineData("registry.example.com", "http://registry.example.com")]
    [InlineData("registry.example.com", "https://registry.example.com/v1/")]
    [InlineData("REGISTRY.EXAMPLE.COM", "registry.example.com")]
    public async Task DockerInlineAuthMatchesRegistryScope(
        string requestedRegistry,
        string configuredRegistry)
    {
        DockerCredentials credentials = await GetInlineCredentialsAsync(
            requestedRegistry,
            configuredRegistry,
            RegistryConfigFormat.Docker);

        Assert.Equal("selected-user", credentials.Username);
    }

    [Theory]
    [InlineData("docker.io")]
    [InlineData("index.docker.io")]
    [InlineData("registry-1.docker.io")]
    [InlineData("https://index.docker.io/v1/")]
    public async Task DockerHubAliasesMatchHistoricalAuthKey(string requestedRegistry)
    {
        DockerCredentials credentials = await GetInlineCredentialsAsync(
            requestedRegistry,
            RegistryReference.DockerHubAuthKey,
            RegistryConfigFormat.Docker);

        Assert.Equal("selected-user", credentials.Username);
    }

    [Theory]
    [InlineData("docker.io", "docker.io")]
    [InlineData("docker.io", "https://docker.io/v1/")]
    [InlineData("index.docker.io", "https://registry-1.docker.io/v2/")]
    [InlineData("registry-1.docker.io", "https://docker.io/v1/")]
    public async Task DockerHubAliasesMatchLegacyInlineAuthKeys(
        string requestedRegistry,
        string configuredRegistry)
    {
        DockerCredentials credentials = await GetInlineCredentialsAsync(
            requestedRegistry,
            configuredRegistry,
            RegistryConfigFormat.Docker);

        Assert.Equal("selected-user", credentials.Username);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DockerHubHistoricalKeyTakesPriorityOverLegacyAlias(bool historicalKeyFirst)
    {
        string historicalEntry =
            $"\"{RegistryReference.DockerHubAuthKey}\": " +
            $"{{ \"auth\": \"{EncodeCredentials("historical-user")}\" }}";
        string aliasEntry =
            $"\"https://docker.io/v1/\": " +
            $"{{ \"auth\": \"{EncodeCredentials("alias-user")}\" }}";
        string entries = historicalKeyFirst
            ? $"{historicalEntry},{aliasEntry}"
            : $"{aliasEntry},{historicalEntry}";
        string config = $"{{ \"auths\": {{ {entries} }} }}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            "docker.io",
            config,
            RegistryConfigFormat.Docker);

        Assert.Equal("historical-user", credentials.Username);
    }

    [Theory]
    [InlineData("docker.io", "index.docker.io")]
    [InlineData("index.docker.io", "docker.io")]
    [InlineData("registry-1.docker.io", "docker.io")]
    [InlineData("docker.io", "registry-1.docker.io")]
    [InlineData("registry-1.docker.io/library/alpine", "docker.io")]
    [InlineData("registry-1.docker.io/library/alpine", "https://docker.io/v1/")]
    public async Task ContainersDockerHubAliasesMatchRegistryAuth(
        string requestedRegistry,
        string configuredRegistry)
    {
        DockerCredentials credentials = await GetInlineCredentialsAsync(
            requestedRegistry,
            configuredRegistry,
            RegistryConfigFormat.Containers);

        Assert.Equal("selected-user", credentials.Username);
    }

    [Fact]
    public async Task ContainersDockerHubAliasesRequireExactCasing()
    {
        const string Registry = "DOCKER.IO/library/alpine";
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"docker.io\": {{ \"auth\": \"{EncodeCredentials("unexpected-user")}\" }}" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Containers);

        await Assert.ThrowsAsync<CredsNotFoundException>(() =>
            CredsProvider.GetCredentialsAsync(
                Registry,
                fileSystem.Object,
                Mock.Of<IProcessService>(),
                environment.Object));
    }

    [Fact]
    public async Task ContainersDockerHubAliasDoesNotMatchBareKeyWithTrailingSlash()
    {
        const string Registry = "index.docker.io/library/alpine";
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"docker.io/\": {{ \"auth\": \"{EncodeCredentials("unexpected-user")}\" }}" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Containers);

        await Assert.ThrowsAsync<CredsNotFoundException>(() =>
            CredsProvider.GetCredentialsAsync(
                Registry,
                fileSystem.Object,
                Mock.Of<IProcessService>(),
                environment.Object));
    }

    [Fact]
    public async Task DockerInlineAuthPrefersCanonicalKeyRegardlessOfPropertyOrder()
    {
        const string Registry = "registry.example.com";
        string legacyAuth = EncodeCredentials("legacy-user");
        string canonicalAuth = EncodeCredentials("canonical-user");
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"https://registry.example.com/v1/\": {{ \"auth\": \"{legacyAuth}\" }}," +
                    $"\"registry.example.com\": {{ \"auth\": \"{canonicalAuth}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            Registry,
            config,
            RegistryConfigFormat.Docker);

        Assert.Equal("canonical-user", credentials.Username);
    }

    [Theory]
    [InlineData("registry.example.com/team/image", "registry.example.com/team/image")]
    [InlineData("https://registry.example.com/team/image", "registry.example.com/team/image")]
    [InlineData("registry.example.com:5000/team/image", "registry.example.com:5000/team/image")]
    [InlineData("registry.example.com/team/image", "registry.example.com")]
    public async Task ContainersInlineAuthMatchesExactNamespaceThenHost(
        string requestedRegistry,
        string configuredRegistry)
    {
        DockerCredentials credentials = await GetInlineCredentialsAsync(
            requestedRegistry,
            configuredRegistry,
            RegistryConfigFormat.Containers);

        Assert.Equal("selected-user", credentials.Username);
    }

    [Theory]
    [InlineData("registry.example.com/team/", "registry.example.com/team")]
    [InlineData("registry.example.com/", "registry.example.com")]
    public async Task ContainersInlineAuthPrefersExactTrailingSlashKey(
        string requestedRegistry,
        string fallbackRegistry)
    {
        string fallbackAuth = EncodeCredentials("fallback-user");
        string exactAuth = EncodeCredentials("exact-user");
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"{fallbackRegistry}\": {{ \"auth\": \"{fallbackAuth}\" }}," +
                    $"\"{requestedRegistry}\": {{ \"auth\": \"{exactAuth}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            requestedRegistry,
            config,
            RegistryConfigFormat.Containers);

        Assert.Equal("exact-user", credentials.Username);
    }

    [Fact]
    public async Task ContainersInlineAuthFallsBackFromTrailingSlashToNormalizedNamespace()
    {
        const string Registry = "registry.example.com/team/";
        string hostAuth = EncodeCredentials("host-user");
        string namespaceAuth = EncodeCredentials("namespace-user");
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"registry.example.com\": {{ \"auth\": \"{hostAuth}\" }}," +
                    $"\"registry.example.com/team\": {{ \"auth\": \"{namespaceAuth}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            Registry,
            config,
            RegistryConfigFormat.Containers);

        Assert.Equal("namespace-user", credentials.Username);
    }

    [Fact]
    public async Task ContainersInlineAuthUsesExactHostWhenNamespaceHostCaseDiffers()
    {
        const string Registry = "REGISTRY.example.com/team/image";
        string hostAuth = EncodeCredentials("host-user");
        string namespaceAuth = EncodeCredentials("namespace-user");
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"registry.example.com/team/image\": {{ \"auth\": \"{namespaceAuth}\" }}," +
                    $"\"REGISTRY.example.com\": {{ \"auth\": \"{hostAuth}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            Registry,
            config,
            RegistryConfigFormat.Containers);

        Assert.Equal("host-user", credentials.Username);
    }

    [Fact]
    public async Task ContainersInlineAuthDoesNotIgnoreNamespaceCase()
    {
        const string Registry = "registry.example.com/Team/image";
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"registry.example.com/team/image\": {{ \"auth\": \"{EncodeCredentials("unexpected-user")}\" }}" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Containers);

        await Assert.ThrowsAsync<CredsNotFoundException>(() =>
            CredsProvider.GetCredentialsAsync(
                Registry,
                fileSystem.Object,
                Mock.Of<IProcessService>(),
                environment.Object));
    }

    [Fact]
    public async Task ContainersInlineAuthPrefersExactNamespaceRegardlessOfPropertyOrder()
    {
        const string Registry = "registry.example.com/team/image";
        string hostAuth = EncodeCredentials("host-user");
        string namespaceAuth = EncodeCredentials("namespace-user");
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"registry.example.com\": {{ \"auth\": \"{hostAuth}\" }}," +
                    $"\"registry.example.com/team/image\": {{ \"auth\": \"{namespaceAuth}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            Registry,
            config,
            RegistryConfigFormat.Containers);

        Assert.Equal("namespace-user", credentials.Username);
    }

    [Fact]
    public async Task InlineAuthUsesLastDuplicateKey()
    {
        const string Registry = "registry.example.com";
        string oldAuth = EncodeCredentials("old-user");
        string newAuth = EncodeCredentials("new-user");
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"{Registry}\": {{ \"auth\": \"{oldAuth}\" }}," +
                    $"\"{Registry}\": {{ \"auth\": \"{newAuth}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            Registry,
            config,
            RegistryConfigFormat.Containers);

        Assert.Equal("new-user", credentials.Username);
    }

    [Theory]
    [InlineData(
        true,
        "registry.example.com",
        "https://registry.example.com/v1/")]
    [InlineData(
        false,
        "docker.io",
        "https://index.docker.io/v1/")]
    public async Task NormalizedInlineAuthUsesLastDuplicateKey(
        bool useDockerConfig,
        string requestedRegistry,
        string configuredRegistry)
    {
        RegistryConfigFormat format = useDockerConfig
            ? RegistryConfigFormat.Docker
            : RegistryConfigFormat.Containers;
        string oldAuth = EncodeCredentials("old-user");
        string newAuth = EncodeCredentials("new-user");
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"{configuredRegistry}\": {{ \"auth\": \"{oldAuth}\" }}," +
                    $"\"{configuredRegistry}\": {{ \"auth\": \"{newAuth}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            requestedRegistry,
            config,
            format);

        Assert.Equal("new-user", credentials.Username);
    }

    [Theory]
    [InlineData("https://registry.example.com/team/image", "registry.example.com")]
    [InlineData("docker.io/library/alpine", "https://index.docker.io/v1/")]
    [InlineData("registry-1.docker.io", "https://index.docker.io/v1/")]
    public async Task DockerCredentialHelperReceivesCanonicalMatchedKey(
        string requestedRegistry,
        string configuredRegistry)
    {
        const string Helper = "example";
        string config =
            "{" +
                "\"credHelpers\": {" +
                    $"\"{configuredRegistry}\": \"{Helper}\"" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Docker);
        AddCredentialHelper(fileSystem, environment, Helper);

        Mock<IProcessService> processService = new();
        processService.StubHelperSuccess(
            Helper,
            configuredRegistry,
            "{ \"Username\": \"helper-user\", \"Secret\": \"helper-password\" }");

        DockerCredentials credentials = await CredsProvider.GetCredentialsAsync(
            requestedRegistry,
            fileSystem.Object,
            processService.Object,
            environment.Object);

        Assert.Equal("helper-user", credentials.Username);
        processService.VerifyAll();
    }

    [Fact]
    public async Task CredentialHelperUsesLastDuplicateKey()
    {
        const string Registry = "registry.example.com";
        const string Helper = "new-helper";
        string config =
            "{" +
                "\"credHelpers\": {" +
                    $"\"{Registry}\": \"old-helper\"," +
                    $"\"{Registry}\": \"{Helper}\"" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Docker);
        AddCredentialHelper(fileSystem, environment, Helper);

        Mock<IProcessService> processService = new();
        processService.StubHelperSuccess(
            Helper,
            Registry,
            "{ \"Username\": \"helper-user\", \"Secret\": \"helper-password\" }");

        DockerCredentials credentials = await CredsProvider.GetCredentialsAsync(
            Registry,
            fileSystem.Object,
            processService.Object,
            environment.Object);

        Assert.Equal("helper-user", credentials.Username);
        processService.VerifyAll();
    }

    [Fact]
    public async Task DockerCredentialHelperDoesNotMatchRepositoryNamespace()
    {
        const string Registry = "registry.example.com/team";
        string config =
            "{" +
                "\"credHelpers\": {" +
                    $"\"{Registry}\": \"example\"" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Docker);

        await Assert.ThrowsAsync<CredsNotFoundException>(() =>
            CredsProvider.GetCredentialsAsync(
                Registry,
                fileSystem.Object,
                Mock.Of<IProcessService>(),
                environment.Object));
    }

    [Fact]
    public async Task DockerCredentialHelperDoesNotMatchDockerHubAlias()
    {
        string config =
            "{" +
                "\"credHelpers\": {" +
                    "\"docker.io\": \"example\"" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Docker);

        await Assert.ThrowsAsync<CredsNotFoundException>(() =>
            CredsProvider.GetCredentialsAsync(
                "index.docker.io",
                fileSystem.Object,
                Mock.Of<IProcessService>(),
                environment.Object));
    }

    [Fact]
    public async Task DockerCredentialHelperRequiresExactDockerHubCasing()
    {
        string config =
            "{" +
                "\"credHelpers\": {" +
                    $"\"{RegistryReference.DockerHubAuthKey}\": \"example\"" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Docker);

        await Assert.ThrowsAsync<CredsNotFoundException>(() =>
            CredsProvider.GetCredentialsAsync(
                "DOCKER.IO",
                fileSystem.Object,
                Mock.Of<IProcessService>(),
                environment.Object));
    }

    [Fact]
    public async Task ContainersCredentialHelperMatchesOnlyRegistryScope()
    {
        const string Registry = "registry.example.com/team/image";
        const string ConfiguredRegistry = "registry.example.com";
        const string Helper = "example";
        string config =
            "{" +
                "\"credHelpers\": {" +
                    $"\"{ConfiguredRegistry}\": \"{Helper}\"," +
                    $"\"{Registry}\": \"namespace-helper\"" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Containers);
        AddCredentialHelper(fileSystem, environment, Helper);

        Mock<IProcessService> processService = new();
        processService.StubHelperSuccess(
            Helper,
            ConfiguredRegistry,
            "{ \"Username\": \"helper-user\", \"Secret\": \"helper-password\" }");

        DockerCredentials credentials = await CredsProvider.GetCredentialsAsync(
            Registry,
            fileSystem.Object,
            processService.Object,
            environment.Object);

        Assert.Equal("helper-user", credentials.Username);
        processService.VerifyAll();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CredentialHelperRequiresExactRegistryKeyCasing(bool useDockerConfig)
    {
        const string Registry = "registry.example.com";
        RegistryConfigFormat format = useDockerConfig
            ? RegistryConfigFormat.Docker
            : RegistryConfigFormat.Containers;
        string config =
            "{" +
                "\"credHelpers\": {" +
                    "\"REGISTRY.EXAMPLE.COM\": \"example\"" +
                "}" +
            "}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, format);

        await Assert.ThrowsAsync<CredsNotFoundException>(() =>
            CredsProvider.GetCredentialsAsync(
                Registry,
                fileSystem.Object,
                Mock.Of<IProcessService>(),
                environment.Object));
    }

    [Theory]
    [InlineData("https://registry.example.com/team/image", "registry.example.com")]
    [InlineData("docker.io/library/alpine", "https://index.docker.io/v1/")]
    [InlineData("DOCKER.IO/library/alpine", "DOCKER.IO")]
    public async Task DockerGlobalCredentialStoreReceivesCanonicalKey(
        string requestedRegistry,
        string expectedHelperInput)
    {
        const string Helper = "example";
        string config = $"{{ \"credsStore\": \"{Helper}\" }}";
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, RegistryConfigFormat.Docker);
        AddCredentialHelper(fileSystem, environment, Helper);

        Mock<IProcessService> processService = new();
        processService.StubHelperSuccess(
            Helper,
            expectedHelperInput,
            "{ \"Username\": \"helper-user\", \"Secret\": \"helper-password\" }");

        await CredsProvider.GetCredentialsAsync(
            requestedRegistry,
            fileSystem.Object,
            processService.Object,
            environment.Object);

        processService.VerifyAll();
    }

    [Fact]
    public async Task ContainersAuthIgnoresGlobalCredentialStore()
    {
        const string Registry = "registry.example.com";
        string config =
            "{" +
                "\"credsStore\": \"obsolete\"," +
                "\"auths\": {" +
                    $"\"{Registry}\": {{ \"auth\": \"{EncodeCredentials("inline-user")}\" }}" +
                "}" +
            "}";

        DockerCredentials credentials = await GetCredentialsFromConfigAsync(
            Registry,
            config,
            RegistryConfigFormat.Containers);

        Assert.Equal("inline-user", credentials.Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" registry.example.com")]
    [InlineData("registry.example.com ")]
    [InlineData("ftp://registry.example.com")]
    [InlineData("https://user@registry.example.com")]
    [InlineData("https://registry.example.com?query")]
    [InlineData("https://registry.example.com#fragment")]
    [InlineData("https://")]
    [InlineData("https://::1:5000/team")]
    [InlineData("[::ffff:192.0.2.1]")]
    [InlineData("[fe80::1%5]")]
    [InlineData("[2001:db8:3:4::192.0.2.33]:5000")]
    [InlineData("2001:db8:3:4::192.0.2.33")]
    [InlineData("https://[2001:db8:3:4::192.0.2.33]:5000/team")]
    [InlineData("registry.example.com:not-a-port")]
    [InlineData("registry.example.com:+5000")]
    [InlineData("registry_1.example.com")]
    [InlineData("registry.example-")]
    [InlineData("registry.example.com:0")]
    [InlineData("registry.example.com:65536")]
    [InlineData("registry.example.com//team")]
    [InlineData("registry.example.com/team//")]
    [InlineData("registry.example.com/team/image:latest")]
    [InlineData("registry.example.com/team/image@sha256:digest")]
    [InlineData("registry.example.com/team\u0000image")]
    [InlineData("registry.example.com/team\u007Fimage")]
    public async Task InvalidRegistryThrowsArgumentExceptionBeforeExternalAccess(string registry)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CredsProvider.GetCredentialsAsync(
                registry,
                Mock.Of<IFileSystem>(MockBehavior.Strict),
                Mock.Of<IProcessService>(MockBehavior.Strict),
                Mock.Of<IEnvironment>(MockBehavior.Strict)));
    }

    private static async Task<DockerCredentials> GetInlineCredentialsAsync(
        string requestedRegistry,
        string configuredRegistry,
        RegistryConfigFormat format)
    {
        string config =
            "{" +
                "\"auths\": {" +
                    $"\"{configuredRegistry}\": {{" +
                        $"\"auth\": \"{EncodeCredentials("selected-user")}\"" +
                    "}" +
                "}" +
            "}";

        return await GetCredentialsFromConfigAsync(requestedRegistry, config, format);
    }

    private static async Task<DockerCredentials> GetCredentialsFromConfigAsync(
        string requestedRegistry,
        string config,
        RegistryConfigFormat format)
    {
        (Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =
            CreateConfig(config, format);

        return await CredsProvider.GetCredentialsAsync(
            requestedRegistry,
            fileSystem.Object,
            Mock.Of<IProcessService>(),
            environment.Object);
    }

    private static (
        Mock<IFileSystem> FileSystem,
        Mock<IEnvironment> Environment) CreateConfig(
            string content,
            RegistryConfigFormat format)
    {
        Mock<IEnvironment> environment = new();
        environment.WithSystemProfileFolder();

        string configPath;
        if (format == RegistryConfigFormat.Containers)
        {
            configPath = Path.Combine("auth", "auth.json");
            environment
                .Setup(value => value.GetEnvironmentVariable("REGISTRY_AUTH_FILE"))
                .Returns(configPath);
        }
        else
        {
            string dockerConfigDirectory = Path.Combine("auth", "docker");
            configPath = Path.Combine(dockerConfigDirectory, "config.json");
            environment
                .Setup(value => value.GetEnvironmentVariable("DOCKER_CONFIG"))
                .Returns(dockerConfigDirectory);
        }

        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(configPath, content);
        return (fileSystem, environment);
    }

    private static void AddCredentialHelper(
        Mock<IFileSystem> fileSystem,
        Mock<IEnvironment> environment,
        string helper)
    {
        string path = Path.Combine("helpers");
        fileSystem.WithFile(Path.Combine(path, $"docker-credential-{helper}"));
        environment.Setup(value => value.GetEnvironmentVariable("PATH")).Returns(path);
        environment.Setup(value => value.GetEnvironmentVariable("PATHEXT")).Returns((string?)null);
    }

    private static string EncodeCredentials(string username) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:password"));
}
