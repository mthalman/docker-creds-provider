using Moq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class ConfigDiscoveryTests
{
    private readonly IEnvironment _defaultEnvironmentMock = new Mock<IEnvironment>().WithTestEnvironment().Object;

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task LookupUsesFirstMatchingFile(int winningIndex, bool earlierFilesExist)
    {
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        string[] paths =
        {
            Path.Combine("runtime", "containers", "auth.json"),
            Path.Combine("config-home", "containers", "auth.json"),
            Path.Combine("docker-config", "config.json")
        };
        Mock<IFileSystem> fileSystem = new();
        for (int index = 0; index < paths.Length; index++)
        {
            if (index >= winningIndex || earlierFilesExist)
            {
                fileSystem.WithFile(
                    paths[index],
                    CreateInlineConfig(index < winningIndex ? "other.example.com" : "registry.example.com", $"user-{index}"));
            }
        }
        fileSystem.WithFile(
            Path.Combine("config-alias", "containers", "auth.json"),
            CreateInlineConfig("registry.example.com", "alias-user"));

        DockerCredentials credentials = await ResolveAsync(fileSystem, environment);

        Assert.Equal($"user-{winningIndex}", credentials.Username);
        Assert.Equal("password", credentials.Password);
        Assert.Null(credentials.IdentityToken);
        for (int index = 0; index <= winningIndex; index++)
        {
            fileSystem.Verify(value => value.FileExists(paths[index]), Times.Once);
        }
        for (int index = winningIndex + 1; index < paths.Length; index++)
        {
            fileSystem.Verify(value => value.FileExists(paths[index]), Times.Never);
        }
        fileSystem.Verify(
            value => value.FileExists(Path.Combine("config-alias", "containers", "auth.json")),
            Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UnsetEnvironmentUsesProfileDefaults(string? unsetValue)
    {
        Mock<IEnvironment> environment = new();
        environment.WithTestEnvironment();
        environment.Setup(value => value.GetEnvironmentVariable(It.IsAny<string>())).Returns(unsetValue);
        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(
            Path.Combine(MockExtensions.TestProfileDirectory, ".config", "containers", "auth.json"),
            CreateInlineConfig("registry.example.com", "containers-user"));
        fileSystem.WithFile(
            Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json"),
            CreateInlineConfig("registry.example.com", "docker-user"));

        DockerCredentials credentials = await ResolveAsync(fileSystem, environment);

        Assert.Equal("containers-user", credentials.Username);
        Assert.Equal(
            new[]
            {
                Path.Combine(MockExtensions.TestProfileDirectory, ".config", "containers", "auth.json"),
                Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json")
            },
            CredsProvider.GetConfigFilePaths(environment.Object));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DeprecatedConfigAliasIsUsedOnlyWhenHomeIsUnset(string? configHome)
    {
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        environment.Setup(value => value.GetEnvironmentVariable("XDG_CONFIG_HOME")).Returns(configHome);
        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(
            Path.Combine("config-alias", "containers", "auth.json"),
            CreateInlineConfig("registry.example.com", "alias-user"));
        fileSystem.WithFile(
            Path.Combine("docker-config", "config.json"),
            CreateInlineConfig("registry.example.com", "docker-user"));

        DockerCredentials credentials = await ResolveAsync(fileSystem, environment);

        Assert.Equal("alias-user", credentials.Username);
    }

    [Fact]
    public async Task MissingConfigHomeFileDoesNotFallBackToDeprecatedAlias()
    {
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(
            Path.Combine("config-alias", "containers", "auth.json"),
            CreateInlineConfig("registry.example.com", "alias-user"));
        fileSystem.WithFile(
            Path.Combine("docker-config", "config.json"),
            CreateInlineConfig("registry.example.com", "docker-user"));

        DockerCredentials credentials = await ResolveAsync(fileSystem, environment);

        Assert.Equal("docker-user", credentials.Username);
        fileSystem.Verify(
            value => value.FileExists(Path.Combine("config-home", "containers", "auth.json")),
            Times.Once);
        fileSystem.Verify(
            value => value.FileExists(Path.Combine("config-alias", "containers", "auth.json")),
            Times.Never);
    }

    [Theory]
    [InlineData("match")]
    [InlineData("no-match")]
    [InlineData("missing")]
    public async Task RegistryAuthOverrideExcludesEveryOtherFile(string overrideState)
    {
        const string OverridePath = "override-auth.json";
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        environment.Setup(value => value.GetEnvironmentVariable("REGISTRY_AUTH_FILE")).Returns(OverridePath);
        Mock<IFileSystem> fileSystem = new();
        foreach (string path in new[]
        {
            Path.Combine("runtime", "containers", "auth.json"),
            Path.Combine("config-home", "containers", "auth.json"),
            Path.Combine("config-alias", "containers", "auth.json"),
            Path.Combine("docker-config", "config.json"),
            Path.Combine(MockExtensions.TestProfileDirectory, ".config", "containers", "auth.json"),
            Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json")
        })
        {
            fileSystem.WithFile(path, CreateInlineConfig("registry.example.com", "excluded-user"));
        }
        if (overrideState != "missing")
        {
            fileSystem.WithFile(
                OverridePath,
                CreateInlineConfig(overrideState == "match" ? "registry.example.com" : "other.example.com", "override-user"));
        }

        if (overrideState == "match")
        {
            DockerCredentials credentials = await ResolveAsync(fileSystem, environment);
            Assert.Equal("override-user", credentials.Username);
        }
        else if (overrideState == "no-match")
        {
            await Assert.ThrowsAsync<CredsNotFoundException>(() => ResolveAsync(fileSystem, environment));
        }
        else
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => ResolveAsync(fileSystem, environment));
        }

        fileSystem.Verify(value => value.FileExists(OverridePath), Times.Once);
        fileSystem.Verify(value => value.FileOpenRead(OverridePath), overrideState == "missing" ? Times.Never() : Times.Once());
        fileSystem.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("XDG_RUNTIME_DIR", "runtime", true)]
    [InlineData("XDG_CONFIG_HOME", "config-home", true)]
    [InlineData("XDG_CONFIG_DIR", "config-alias", true)]
    [InlineData("DOCKER_CONFIG", "docker-config", false)]
    [InlineData("REGISTRY_AUTH_FILE", "override-auth.json", true)]
    public async Task DiscoveryLocationDeterminesRegistryMatchingFormat(
        string variable,
        string location,
        bool containersFormat)
    {
        Mock<IEnvironment> environment = new();
        environment.WithTestEnvironment();
        environment.Setup(value => value.GetEnvironmentVariable(variable)).Returns(location);
        string path = variable == "REGISTRY_AUTH_FILE"
            ? location
            : containersFormat
                ? Path.Combine(location, "containers", "auth.json")
                : Path.Combine(location, "config.json");
        string config = JsonSerializer.Serialize(new
        {
            auths = new Dictionary<string, object>
            {
                ["registry.example.com"] = new { auth = EncodeCredentials("host-user") },
                ["registry.example.com/team"] = new { auth = EncodeCredentials("namespace-user") }
            }
        });
        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(path, config);

        DockerCredentials credentials = await CredsProvider.GetCredentialsAsync(
            "registry.example.com/team/image",
            fileSystem.Object,
            Mock.Of<IProcessService>(MockBehavior.Strict),
            environment.Object);

        Assert.Equal(containersFormat ? "namespace-user" : "host-user", credentials.Username);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{ \"auths\": ")]
    public async Task MalformedConfigJsonFailsWithoutTryingLaterFiles(string config)
    {
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        Mock<IFileSystem> fileSystem = CreateFirstConfigWithFallback(config);

        await Assert.ThrowsAnyAsync<JsonException>(() => ResolveAsync(fileSystem, environment));

        AssertLaterFilesNotProbed(fileSystem);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("\"config\"")]
    [InlineData("{ \"auths\": null }")]
    [InlineData("{ \"auths\": [] }")]
    [InlineData("{ \"auths\": 1 }")]
    [InlineData("{ \"auths\": true }")]
    [InlineData("{ \"auths\": \"invalid\" }")]
    [InlineData("{ \"credHelpers\": null }")]
    [InlineData("{ \"credHelpers\": [] }")]
    [InlineData("{ \"credHelpers\": 1 }")]
    [InlineData("{ \"credHelpers\": true }")]
    [InlineData("{ \"credHelpers\": \"invalid\" }")]
    public async Task WrongConfigJsonTypeFailsWithoutTryingLaterFiles(string config)
    {
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        Mock<IFileSystem> fileSystem = CreateFirstConfigWithFallback(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(fileSystem, environment));

        AssertLaterFilesNotProbed(fileSystem);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"auths\": {} }")]
    [InlineData("{ \"credHelpers\": {} }")]
    public async Task MissingMatchingEntryContinuesToLaterFile(string config)
    {
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        Mock<IFileSystem> fileSystem = CreateFirstConfigWithFallback(config);

        DockerCredentials credentials = await ResolveAsync(fileSystem, environment);

        Assert.Equal("fallback-user", credentials.Username);
    }

    [Theory]
    [InlineData("{ \"auths\": { \"registry.example.com\": {} } }", "auth")]
    [InlineData("{ \"auths\": { \"registry.example.com\": { \"auth\": null } } }", "auth")]
    [InlineData("{ \"credHelpers\": { \"registry.example.com\": null } }", "credHelper")]
    public async Task SelectedEntryMissingRequiredValueDoesNotFallBack(string config, string field)
    {
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        Mock<IFileSystem> fileSystem = CreateFirstConfigWithFallback(config);

        JsonException exception = await Assert.ThrowsAsync<JsonException>(() => ResolveAsync(fileSystem, environment));

        Assert.Contains(field, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry.example.com", exception.Message);
        Assert.Contains(Path.Combine("runtime", "containers", "auth.json"), exception.Message);
        AssertLaterFilesNotProbed(fileSystem);
    }

    [Fact]
    public async Task SelectedHelperFailureDoesNotFallBack()
    {
        const string Helper = "selected-helper";
        Mock<IEnvironment> environment = CreateDiscoveryEnvironment();
        environment.WithPath(new List<string> { "helpers" });
        Mock<IFileSystem> fileSystem = CreateFirstConfigWithFallback(
            $"{{ \"credHelpers\": {{ \"registry.example.com\": \"{Helper}\" }} }}");
        fileSystem.WithFile(Path.Combine("helpers", $"docker-credential-{Helper}"));
        Mock<IProcessService> processService = new();
        processService.StubHelperError(Helper, "registry.example.com", "unavailable");

        await Assert.ThrowsAsync<CredsNotFoundException>(() => CredsProvider.GetCredentialsAsync(
            "registry.example.com", fileSystem.Object, processService.Object, environment.Object));

        processService.VerifyAll();
        AssertLaterFilesNotProbed(fileSystem);
    }

    [Fact]
    public async Task ConfigFileDoesNotExist()
    {
        string dockerConfigPath = Path.Combine(
            MockExtensions.TestProfileDirectory,
            ".docker",
            "config.json");

        Mock<IFileSystem> fileSystemMock = new();
        fileSystemMock
            .Setup(o => o.FileExists(dockerConfigPath))
            .Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => CredsProvider.GetCredentialsAsync("test", fileSystemMock.Object, Mock.Of<IProcessService>(), _defaultEnvironmentMock));
    }

    [Fact]
    public async Task NativeStore_UsesDockerConfigEnvironmentVariable() {
        var tempPath = Path.Combine("unit-test-config", "docker");
        var dockerConfigPath = Path.Combine(tempPath, "config.json");

        Mock<IEnvironment> envMock = new();
        envMock.WithTestEnvironment();
        envMock.Setup(e => e.GetEnvironmentVariable("DOCKER_CONFIG")).Returns(tempPath);

        string username = "foo";
        string password = "<CREDENTIAL_PLACEHOLDER>";

        string encodedCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

        string dockerConfigContent =
            "{" +
                "\"auths\": {" +
                    "\"dummyRegistry.io\": {" +
                        $"\"auth\": \"{encodedCreds}\"" +
                    "}" +
                "}" +
            "}";

        Mock<IFileSystem> fileSystemMock = new();
        fileSystemMock
            .WithFile(dockerConfigPath, dockerConfigContent);

        var creds = await CredsProvider.GetCredentialsAsync("dummyRegistry.io", fileSystemMock.Object, Mock.Of<ProcessService>(), envMock.Object);
        Assert.Equal("foo", creds.Username);
        Assert.Equal(password, creds.Password);
    }

    [Fact]
    public async Task UsesAllConfigPaths()
    {
        Mock<IFileSystem> fileSystemMock = new();
        string[] configFilePaths = new[]
        {
            Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json"),
            Path.Combine(MockExtensions.TestProfileDirectory, ".config", "containers", "auth.json"),
        };
        for (int idx = 0; idx < configFilePaths.Length; idx++)
        {
            string dockerConfigPath = configFilePaths[idx];
            string registry = $"testregistry{idx}";
            string encodedCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"testuser{idx}:testpass{idx}"));
            string dockerConfigContent =
            "{" +
                "\"auths\": {" +
                    $"\"{registry}\": {{" +
                        $"\"auth\": \"{encodedCreds}\"" +
                    "}" +
                "}" +
            "}";

            fileSystemMock.WithFile(dockerConfigPath, dockerConfigContent);
        }

        for (int idx = 0; idx < configFilePaths.Length; idx++)
        {
            string registry = $"testregistry{idx}";
            DockerCredentials creds = await CredsProvider.GetCredentialsAsync(registry, fileSystemMock.Object, Mock.Of<IProcessService>(), _defaultEnvironmentMock);

            Assert.Equal($"testuser{idx}", creds.Username);
            Assert.Equal($"testpass{idx}", creds.Password);
            Assert.Null(creds.IdentityToken);
        }
    }

    [Theory]
    // REGISTRY_AUTH_FILE replaces all
    [InlineData("registryauthfile", "xdgruntimedir", "xdgconfighome", "xdgconfigdir", "dockerconfigdir", new[] { "registryauthfile" } )]
    // Order of different paths.
    [InlineData("",                 "xdgruntimedir", "xdgconfighome", "xdgconfigdir", "dockerconfigdir", new[] { "xdgruntimedir/containers/auth.json",
                                                                                                                "xdgconfighome/containers/auth.json",
                                                                                                                "dockerconfigdir/config.json" } )]
    // XDG_CONFIG_DIR is used when XDG_CONFIG_HOME is unset.
    [InlineData("",                 "",              "",              "xdgconfigdir", "dockerconfigdir", new[] { "xdgconfigdir/containers/auth.json",
                                                                                                                "dockerconfigdir/config.json" } )]
    // XDG_CONFIG_HOME defaults to $HOME/.config when neither config variable is set.
    [InlineData("",                 "",              "",              "",             "dockerconfigdir", new[] { "userprofile/.config/containers/auth.json",
                                                                                                                "dockerconfigdir/config.json" } )]
    // DOCKER_CONFIG defaults to $HOME/.docker
    [InlineData("",                 "",              "",              "",             "",                new[] { "userprofile/.config/containers/auth.json",
                                                                                                                "userprofile/.docker/config.json" } )]
    public void ConfigFilePaths(string? registryAuthFile, string? xdgRuntimeDir, string? xdgConfigHome, string? xdgConfigDir, string? dockerConfig, string[] expectedConfigFilePaths)
    {
        for (int i = 0; i < expectedConfigFilePaths.Length; i++)
        {
            // Windows: use backslash path separators.
            expectedConfigFilePaths[i] = expectedConfigFilePaths[i].Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        Mock<IEnvironment> envMock = new();
        envMock.Setup(o => o.GetEnvironmentVariable("REGISTRY_AUTH_FILE")).Returns(registryAuthFile);
        envMock.Setup(o => o.GetEnvironmentVariable("XDG_RUNTIME_DIR")).Returns(xdgRuntimeDir);
        envMock.Setup(o => o.GetEnvironmentVariable("XDG_CONFIG_HOME")).Returns(xdgConfigHome);
        envMock.Setup(o => o.GetEnvironmentVariable("XDG_CONFIG_DIR")).Returns(xdgConfigDir);
        envMock.Setup(o => o.GetEnvironmentVariable("DOCKER_CONFIG")).Returns(dockerConfig);
        envMock.Setup(e => e.GetFolderPath(Environment.SpecialFolder.UserProfile)).Returns("userprofile");

        string[] configFilePath = CredsProvider.GetConfigFilePaths(envMock.Object);
        Assert.Equal(expectedConfigFilePaths, configFilePath);
    }

    private static Mock<IEnvironment> CreateDiscoveryEnvironment()
    {
        Mock<IEnvironment> environment = new();
        environment.WithTestEnvironment();
        environment.Setup(value => value.GetEnvironmentVariable("XDG_RUNTIME_DIR")).Returns("runtime");
        environment.Setup(value => value.GetEnvironmentVariable("XDG_CONFIG_HOME")).Returns("config-home");
        environment.Setup(value => value.GetEnvironmentVariable("XDG_CONFIG_DIR")).Returns("config-alias");
        environment.Setup(value => value.GetEnvironmentVariable("DOCKER_CONFIG")).Returns("docker-config");
        return environment;
    }

    private static Mock<IFileSystem> CreateFirstConfigWithFallback(string firstConfig)
    {
        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(Path.Combine("runtime", "containers", "auth.json"), firstConfig);
        fileSystem.WithFile(
            Path.Combine("config-home", "containers", "auth.json"),
            CreateInlineConfig("registry.example.com", "fallback-user"));
        fileSystem.WithFile(
            Path.Combine("docker-config", "config.json"),
            CreateInlineConfig("registry.example.com", "docker-user"));
        return fileSystem;
    }

    private static void AssertLaterFilesNotProbed(Mock<IFileSystem> fileSystem)
    {
        fileSystem.Verify(value => value.FileExists(Path.Combine("config-home", "containers", "auth.json")), Times.Never);
        fileSystem.Verify(value => value.FileExists(Path.Combine("docker-config", "config.json")), Times.Never);
    }

    private static Task<DockerCredentials> ResolveAsync(Mock<IFileSystem> fileSystem, Mock<IEnvironment> environment) =>
        CredsProvider.GetCredentialsAsync(
            "registry.example.com",
            fileSystem.Object,
            Mock.Of<IProcessService>(MockBehavior.Strict),
            environment.Object);

    private static string CreateInlineConfig(string registry, string username) =>
        JsonSerializer.Serialize(new
        {
            auths = new Dictionary<string, object> { [registry] = new { auth = EncodeCredentials(username) } }
        });

    private static string EncodeCredentials(string username) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:password"));
}
