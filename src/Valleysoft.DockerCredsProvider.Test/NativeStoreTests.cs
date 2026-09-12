using Moq;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class NativeStoreTests
{
    private readonly IEnvironment _defaultEnvironmentMock = new Mock<IEnvironment>().WithTestEnvironment().Object;

    [Theory]
    [InlineData("Username", "null")]
    [InlineData("Username", "1")]
    [InlineData("Username", "true")]
    [InlineData("Username", "[]")]
    [InlineData("Username", "{}")]
    [InlineData("Secret", "null")]
    [InlineData("Secret", "1")]
    [InlineData("Secret", "true")]
    [InlineData("Secret", "[]")]
    [InlineData("Secret", "{}")]
    public async Task InvalidRequiredFieldDoesNotExposeHelperPayload(string field, string value)
    {
        const string Password = "invalid-field-password-sentinel";
        const string Token = "invalid-field-token-sentinel";
        string output = field == "Username"
            ? $"{{ \"Username\": {value}, \"Secret\": \"{Password}\", \"IdentityToken\": \"{Token}\" }}"
            : $"{{ \"Username\": \"testuser\", \"Secret\": {value}, \"Password\": \"{Password}\", \"IdentityToken\": \"{Token}\" }}";
        Mock<IProcessService> processService = new();
        processService.StubHelperSuccess("desktop", "test", output);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetNativeStoreCredentialsAsync(processService.Object));

        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.Contains($"'{field}'", exception.Message);
        AssertExceptionChainDoesNotContain(exception, Password);
        AssertExceptionChainDoesNotContain(exception, Token);
        processService.VerifyAll();
    }

    [Fact]
    public async Task NonzeroExitDoesNotExposeEitherOutputStream()
    {
        const string Password = "stdout-password-sentinel";
        const string Token = "stderr-token-sentinel";
        Mock<IProcessService> processService = new();
        processService.StubHelperError("desktop", "test", Token, Password);

        CredsNotFoundException exception = await Assert.ThrowsAsync<CredsNotFoundException>(
            () => GetNativeStoreCredentialsAsync(processService.Object));

        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.Contains("code 1", exception.Message);
        Assert.Contains("standard output", exception.Message);
        Assert.Contains("standard error", exception.Message);
        AssertExceptionChainDoesNotContain(exception, Password);
        AssertExceptionChainDoesNotContain(exception, Token);
        processService.VerifyAll();
    }

    [Theory]
    [InlineData("testuser", "<CREDENTIAL_PLACEHOLDER>")]
    [InlineData("<token>", "identitytoken")]
    [InlineData("testuser", "")]
    [InlineData("<token>", "")]
    [InlineData("us\u00e9r", "p\u00e4ss:\u79d8\u5bc6")]
    [InlineData("<token>", "token:\u79d8\u5bc6")]
    public async Task HelperCredentialsRetainTheirValues(string username, string secret)
    {
        Mock<IProcessService> processService = new();
        processService.StubHelperSuccess(
            "desktop", "test", JsonSerializer.Serialize(new { Username = username, Secret = secret }));

        DockerCredentials credentials = await GetNativeStoreCredentialsAsync(processService.Object);

        Assert.Equal(username, credentials.Username);
        Assert.Equal(username == "<token>" ? null : secret, credentials.Password);
        Assert.Equal(username == "<token>" ? secret : null, credentials.IdentityToken);
        processService.VerifyAll();
    }

    [Theory]
    [InlineData("credsStore", "1")]
    [InlineData("credsStore", "true")]
    [InlineData("credsStore", "[]")]
    [InlineData("credsStore", "{}")]
    [InlineData("credHelpers", "1")]
    [InlineData("credHelpers", "true")]
    [InlineData("credHelpers", "[]")]
    [InlineData("credHelpers", "{}")]
    public async Task WrongHelperNameTypeThrowsInvalidOperationException(string field, string value)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveHelperConfigAsync(field, value));
    }

    [Theory]
    [InlineData("credsStore")]
    [InlineData("credHelpers")]
    public async Task NullHelperNameThrowsJsonExceptionWithConfigContext(string field)
    {
        JsonException exception = await Assert.ThrowsAsync<JsonException>(
            () => ResolveHelperConfigAsync(field, "null"));

        Assert.Contains(field == "credHelpers" ? "credHelper" : field, exception.Message);
        Assert.Contains(Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json"), exception.Message);
    }

    [Fact]
    public async Task LocatedHelperExecutionFailureRetainsActionableContext()
    {
        Mock<IProcessService> processService = new();
        processService
            .Setup(value => value.RunAsync(
                It.IsAny<ProcessStartInfo>(),
                "test",
                Valleysoft.DockerCredsProvider.NativeStore.HelperOutputLimit,
                Valleysoft.DockerCredsProvider.NativeStore.HelperTimeout,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Win32Exception(2));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetNativeStoreCredentialsAsync(processService.Object));

        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.IsType<Win32Exception>(exception.InnerException);
        processService.VerifyAll();
    }

    [Fact]
    public async Task NativeStore_ExeNotFound()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveHelperConfigAsync("credsStore", "\"desktop\""));

        Assert.Contains("Unable to locate docker-credential-desktop", exception.Message);
    }

    [Theory]
    [InlineData(
        "Username",
        "{ \"Secret\": \"missing-username-secret\" }",
        "missing-username-secret")]
    [InlineData(
        "Secret",
        "{ \"Username\": \"testuser\", \"IdentityToken\": \"missing-secret-token\" }",
        "missing-secret-token")]
    public async Task NativeStore_MissingFieldDoesNotExposeOutput(
        string missingField,
        string output,
        string sensitiveValue)
    {
        Mock<IProcessService> processServiceMock = new();
        processServiceMock.StubHelperSuccess("desktop", "test", output);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetNativeStoreCredentialsAsync(processServiceMock.Object));

        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.Contains($"'{missingField}'", exception.Message);
        Assert.Contains(
            $"({(output + Environment.NewLine).Length} captured characters)",
            exception.Message);
        AssertExceptionChainDoesNotContain(exception, sensitiveValue);
    }

    [Theory]
    [InlineData(
        "{ \"Username\": \"testuser\", \"Secret\": \"malformed-json-secret\"",
        "malformed-json-secret")]
    [InlineData("@", "@")]
    [InlineData("", "absent-payload-sentinel")]
    public async Task NativeStore_MalformedJsonDoesNotExposeOutput(string output, string sensitiveValue)
    {
        Mock<IProcessService> processServiceMock = new();
        processServiceMock.StubHelperSuccess("desktop", "test", output);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetNativeStoreCredentialsAsync(processServiceMock.Object));

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.Contains("malformed JSON", exception.Message);
        Assert.Contains(
            $"{(output + Environment.NewLine).Length} captured characters",
            exception.Message);
        Assert.Contains("line", exception.Message);
        Assert.Contains("byte position", exception.Message);
        AssertExceptionChainDoesNotContain(exception, sensitiveValue);
    }

    [Theory]
    [InlineData("[\"array-root-secret\"]", "array-root-secret", "Array")]
    [InlineData("\"scalar-root-secret\"", "scalar-root-secret", "String")]
    [InlineData("null", "absent-payload-sentinel", "Null")]
    [InlineData("42", "absent-payload-sentinel", "Number")]
    [InlineData("true", "absent-payload-sentinel", "True")]
    public async Task NativeStore_NonObjectJsonDoesNotExposeOutput(
        string output,
        string sensitiveValue,
        string rootKind)
    {
        Mock<IProcessService> processServiceMock = new();
        processServiceMock.StubHelperSuccess("desktop", "test", output);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetNativeStoreCredentialsAsync(processServiceMock.Object));

        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.Contains("invalid response", exception.Message);
        Assert.Contains($"was {rootKind} instead of an object", exception.Message);
        Assert.Contains(
            $"({(output + Environment.NewLine).Length} captured characters)",
            exception.Message);
        AssertExceptionChainDoesNotContain(exception, sensitiveValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeStore_NonzeroExitDoesNotExposeOutput(bool writeSecretToStandardError)
    {
        const string SensitiveValue = "nonzero-exit-secret";
        Mock<IProcessService> processServiceMock = new();
        processServiceMock.StubHelperError(
            "desktop",
            "test",
            writeSecretToStandardError ? SensitiveValue : null,
            writeSecretToStandardError ? null : SensitiveValue);

        CredsNotFoundException exception = await Assert.ThrowsAsync<CredsNotFoundException>(
            () => GetNativeStoreCredentialsAsync(processServiceMock.Object));

        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.Contains("code 1", exception.Message);
        Assert.Contains(
            writeSecretToStandardError
                ? "captured standard output length: 0 characters"
                : "captured standard error length: 0 characters",
            exception.Message);
        Assert.Contains("Helper output was omitted", exception.Message);
        AssertExceptionChainDoesNotContain(exception, SensitiveValue);
    }

    [Theory]
    [InlineData("standard output")]
    [InlineData("standard error")]
    public async Task NativeStore_OversizedOutputIsSanitized(string streamName)
    {
        Mock<IProcessService> processServiceMock = new();
        processServiceMock
            .Setup(o => o.RunAsync(
                It.Is<ProcessStartInfo>(startInfo =>
                    startInfo.CreateNoWindow &&
                    !startInfo.UseShellExecute &&
                    startInfo.RedirectStandardInput &&
                    startInfo.RedirectStandardOutput &&
                    startInfo.RedirectStandardError),
                "test",
                Valleysoft.DockerCredsProvider.NativeStore.HelperOutputLimit,
                Valleysoft.DockerCredsProvider.NativeStore.HelperTimeout,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProcessOutputLimitExceededException(
                streamName,
                Valleysoft.DockerCredsProvider.NativeStore.HelperOutputLimit));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetNativeStoreCredentialsAsync(processServiceMock.Object));

        Assert.Contains("docker-credential-desktop", exception.Message);
        Assert.Contains(streamName, exception.Message);
        Assert.Contains("1048576-byte", exception.Message);
        Assert.Contains("Helper output was omitted", exception.Message);
        processServiceMock.VerifyAll();
    }

    [Fact]
    public async Task NativeStore_CallerCancellationIsPropagated()
    {
        using CancellationTokenSource cancellationSource = new();
        Mock<IProcessService> processServiceMock = new();
        processServiceMock
            .Setup(o => o.RunAsync(
                It.IsAny<ProcessStartInfo>(),
                "test",
                Valleysoft.DockerCredsProvider.NativeStore.HelperOutputLimit,
                Valleysoft.DockerCredsProvider.NativeStore.HelperTimeout,
                cancellationSource.Token))
            .Returns(async (
                ProcessStartInfo _,
                string? _,
                int _,
                TimeSpan _,
                CancellationToken cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new ProcessResult(0, string.Empty, string.Empty);
            });

        Task<DockerCredentials> getCredentialsTask =
            GetNativeStoreCredentialsAsync(processServiceMock.Object, cancellationSource.Token);
        cancellationSource.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getCredentialsTask);

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        processServiceMock.VerifyAll();
    }

    [Fact]
    public async Task NativeStore_HelperTimeoutIsPropagated()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Valleysoft.DockerCredsProvider.NativeStore.HelperTimeout);

        Mock<IProcessService> processServiceMock = new();
        processServiceMock
            .Setup(o => o.RunAsync(
                It.IsAny<ProcessStartInfo>(),
                "test",
                Valleysoft.DockerCredsProvider.NativeStore.HelperOutputLimit,
                Valleysoft.DockerCredsProvider.NativeStore.HelperTimeout,
                CancellationToken.None))
            .ThrowsAsync(new TimeoutException());

        await Assert.ThrowsAsync<TimeoutException>(() =>
            GetNativeStoreCredentialsAsync(processServiceMock.Object));

        processServiceMock.VerifyAll();
    }

    [Fact]
    public async Task NativeStore_ProbesPATHForHelper() {
        string dockerConfigPath = Path.Combine(
            MockExtensions.TestProfileDirectory,
            ".docker",
            "config.json");

        string helper = "example";
        string fullHelperName = $"docker-credential-{helper}";
        string username = "<token>";
        string token = "token";

        string dockerConfigContent =
            "{" +
                "\"credHelpers\": {" +
                    $"\"testregistry\": \"{helper}\"" +
                "}" +
            "}";

        // the idea here is that we setup the helper on the second PATH entry,
        // so if we succeed that means we probed.
        var systemPaths = new List<string>{
            "/a",
            "/b"
        };

        Mock<IFileSystem> fileSystemMock = new();
        fileSystemMock
            .WithFile(dockerConfigPath, dockerConfigContent)
            .WithFile(Path.Combine(systemPaths[1], fullHelperName));

        Mock<IEnvironment> envMock = new();
        envMock
            .WithTestEnvironment()
            .WithPath(systemPaths);
        envMock.Setup(o => o.GetEnvironmentVariable("PATHEXT")).Returns((string?)null);

        Mock<IProcessService> processServiceMock = new();
        processServiceMock.StubHelperSuccess(helper, "testregistry",  $"{{ \"Username\": \"{username}\", \"Secret\": \"{token}\" }}");

        DockerCredentials creds = await CredsProvider.GetCredentialsAsync("testregistry", fileSystemMock.Object, processServiceMock.Object, envMock.Object);

        Assert.Equal(username, creds.Username);
        Assert.Equal(token, creds.IdentityToken);
        Assert.Null(creds.Password);
    }

    [WindowsFact]
    public async Task NativeStore_ProbesPATHEXTForHelperBinaries() {
        string dockerConfigPath = Path.Combine(
            MockExtensions.TestProfileDirectory,
            ".docker",
            "config.json");

        string helper = "example";
        string fullHelperName = $"docker-credential-{helper}";
        string username = "<token>";
        string token = "token";

        string dockerConfigContent =
            "{" +
                "\"credHelpers\": {" +
                    $"\"testregistry\": \"{helper}\"" +
                "}" +
            "}";

        var pathRoot = "/a";
        // the idea here is that we set up the helper with a different extension
        // and prime the system to probe that extension.  if we succeed, that means we
        // probed as expected.
        var pathExts = new List<string>{
            ".ABC",
            ".XYZ"
        };

        Mock<IFileSystem> fileSystemMock = new();
        fileSystemMock
            .WithFile(dockerConfigPath, dockerConfigContent)
            .WithFile(Path.Combine(pathRoot, $"{fullHelperName}{pathExts[0]}"))
            .WithFile(Path.Combine(pathRoot, $"{fullHelperName}{pathExts[1]}"));

        Mock<IEnvironment> envMock = new();
        envMock
            .WithTestEnvironment()
            .WithPath(new List<string> { pathRoot })
            .WithExecutableExtensions(pathExts);

        Mock<IProcessService> processServiceMock = new();
        processServiceMock.StubHelperSuccess($"{helper}{pathExts[0]}", "testregistry",  $"{{ \"Username\": \"{username}\", \"Secret\": \"{token}\" }}");

        DockerCredentials creds = await CredsProvider.GetCredentialsAsync("testregistry", fileSystemMock.Object, processServiceMock.Object, envMock.Object);

        Assert.Equal(username, creds.Username);
        Assert.Equal(token, creds.IdentityToken);
        Assert.Null(creds.Password);
    }

    [WindowsFact]
    public async Task NativeStore_SupportsPATHEXTPrecedence() {
        string dockerConfigPath = Path.Combine(
            MockExtensions.TestProfileDirectory,
            ".docker",
            "config.json");

        string helper = "example";
        string fullHelperName = $"docker-credential-{helper}";
        string username = "<token>";
        string token = "token";

        string dockerConfigContent =
            "{" +
                "\"credHelpers\": {" +
                    $"\"testregistry\": \"{helper}\"" +
                "}" +
            "}";

        var pathRoot = "/a";
        // the idea here is that we set up the helper with a different extension
        // and prime the system to probe that extension.  if we succeed, that means we
        // probed as expected.
        var pathExts = new List<string>{
            ".ABC",
            ".XYZ"
        };

        Mock<IFileSystem> fileSystemMock = new();
        fileSystemMock
            .WithFile(dockerConfigPath, dockerConfigContent)
            .WithFile(Path.Combine(pathRoot, $"{fullHelperName}{pathExts[1]}"));

        Mock<IEnvironment> envMock = new();
        envMock
            .WithTestEnvironment()
            .WithPath(new List<string> { pathRoot })
            .WithExecutableExtensions(pathExts);

        Mock<IProcessService> processServiceMock = new();
        processServiceMock.StubHelperSuccess($"{helper}{pathExts[1]}", "testregistry",  $"{{ \"Username\": \"{username}\", \"Secret\": \"{token}\" }}");

        DockerCredentials creds = await CredsProvider.GetCredentialsAsync("testregistry", fileSystemMock.Object, processServiceMock.Object, envMock.Object);

        Assert.Equal(username, creds.Username);
        Assert.Equal(token, creds.IdentityToken);
        Assert.Null(creds.Password);
    }

    private static Task<DockerCredentials> GetNativeStoreCredentialsAsync(
        IProcessService processService,
        CancellationToken cancellationToken = default)
    {
        const string CredsStore = "desktop";
        const string Registry = "test";
        string dockerConfigPath = Path.Combine(
            MockExtensions.TestProfileDirectory,
            ".docker",
            "config.json");
        string pathRoot = "/a";

        Mock<IFileSystem> fileSystemMock = new();
        fileSystemMock
            .WithFile(dockerConfigPath, $"{{ \"credsStore\": \"{CredsStore}\" }}")
            .WithFile(Path.Combine(pathRoot, $"docker-credential-{CredsStore}"));

        Mock<IEnvironment> envMock = new();
        envMock.WithTestEnvironment();
        envMock.Setup(o => o.GetEnvironmentVariable("PATH")).Returns(pathRoot);
        envMock.Setup(o => o.GetEnvironmentVariable("PATHEXT")).Returns((string?)null);

        return CredsProvider.GetCredentialsAsync(
            Registry,
            fileSystemMock.Object,
            processService,
            envMock.Object,
            cancellationToken);
    }

    private static void AssertExceptionChainDoesNotContain(Exception exception, string sensitiveValue)
    {
        Assert.DoesNotContain(sensitiveValue, exception.ToString());
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            Assert.DoesNotContain(sensitiveValue, current.Message);
        }
    }

    private Task<DockerCredentials> ResolveHelperConfigAsync(string field, string value)
    {
        string config = field == "credHelpers"
            ? $"{{ \"credHelpers\": {{ \"registry.example.com\": {value} }} }}"
            : $"{{ \"credsStore\": {value} }}";
        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(
            Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json"),
            config);
        return CredsProvider.GetCredentialsAsync(
            "registry.example.com",
            fileSystem.Object,
            Mock.Of<IProcessService>(MockBehavior.Strict),
            _defaultEnvironmentMock);
    }
}
