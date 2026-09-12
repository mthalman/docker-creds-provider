using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Moq;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class CredsProviderIntegrationTests
{
    [Theory]
    [InlineData("docker.io/library/alpine", "https://index.docker.io/v1/", false, false)]
    [InlineData("index.docker.io", "https://index.docker.io/v1/", false, false)]
    [InlineData("registry-1.docker.io", "https://index.docker.io/v1/", false, true)]
    [InlineData("https://index.docker.io/v1/", "https://index.docker.io/v1/", false, true)]
    [InlineData("https://registry.example.com:5000/team/image", "registry.example.com:5000", false, false)]
    [InlineData("registry.example.com:5000/team", "registry.example.com:5000", false, true)]
    [InlineData("registry.example.com:5000/team/image", "registry.example.com:5000", true, false)]
    [InlineData("https://[::1]:5000/team", "[::1]:5000", false, false)]
    public async Task GetCredentialsAsync_SendsExactCanonicalUtf8InputAndEof(
        string requestedRegistry,
        string expectedInput,
        bool containersConfig,
        bool globalStore)
    {
        string config = globalStore
            ? JsonSerializer.Serialize(new { credsStore = "test" })
            : CreateHelperConfig(expectedInput);
        await using LookupFixture fixture = new(config, containersConfig);

        DockerCredentials credentials = await fixture.GetCredentialsAsync(requestedRegistry);

        AssertProtocolCredentials(credentials, expectedInput);
        fixture.AssertExitedAndDisposed();
    }

    [Fact]
    public async Task GetCredentialsAsync_DrainsChunkedJsonAndSplitUtf8WithoutFinalNewline()
    {
        const string Registry = "registry.example.com";
        await using LookupFixture fixture = new(CreateHelperConfig(Registry), scenario: "chunked");

        DockerCredentials credentials = await fixture.GetCredentialsAsync(Registry);

        AssertProtocolCredentials(credentials, Registry);
        fixture.AssertExitedAndDisposed();
    }

    [Theory]
    [InlineData("overflow-stdout", "standard output")]
    [InlineData("overflow-stderr", "standard error")]
    public async Task GetCredentialsAsync_OutputLimitFailureIsSanitizedAndCleansUp(
        string scenario,
        string expectedStream)
    {
        const string Registry = "registry.example.com";
        await using LookupFixture fixture = new(CreateHelperConfig(Registry), scenario: scenario);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.GetCredentialsAsync(Registry));

        Assert.Contains("docker-credential-test", exception.Message);
        Assert.Contains(expectedStream, exception.Message);
        Assert.Contains("1048576-byte", exception.Message);
        Assert.DoesNotContain("fixture-overflow-secret", exception.ToString());
        Assert.IsType<ProcessOutputLimitExceededException>(exception.InnerException);
        fixture.AssertExitedAndDisposed();
    }

    [Fact]
    public async Task GetCredentialsAsync_ConcurrentLookupsKeepCredentialsAndCancellationIsolated()
    {
        string[] registries = Enumerable.Range(0, 4)
            .Select(index => $"registry{index}.example.com:5000")
            .ToArray();
        LookupFixture[] fixtures = registries
            .Select(registry => new LookupFixture(CreateHelperConfig(registry), gated: true))
            .ToArray();
        using CancellationTokenSource cancellation = new();

        try
        {
            Task<DockerCredentials>[] lookups = fixtures
                .Select((fixture, index) => fixture.GetCredentialsAsync(
                    registries[index],
                    index == 0 ? cancellation.Token : CancellationToken.None))
                .ToArray();

            await Task.WhenAll(fixtures.Select(fixture => fixture.WaitUntilReadyAsync()));
            Assert.All(lookups, lookup => Assert.False(lookup.IsCompleted));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => lookups[0]);
            fixtures[0].AssertExitedAndDisposed();
            Assert.All(lookups.Skip(1), lookup => Assert.False(lookup.IsCompleted));

            foreach (LookupFixture fixture in fixtures.Skip(1))
            {
                fixture.Release();
            }

            DockerCredentials[] credentials = await Task.WhenAll(lookups.Skip(1));
            for (int index = 0; index < credentials.Length; index++)
            {
                AssertProtocolCredentials(credentials[index], registries[index + 1]);
                fixtures[index + 1].AssertExitedAndDisposed();
            }
        }
        finally
        {
            await Task.WhenAll(fixtures.Select(fixture => fixture.DisposeAsync().AsTask()));
        }
    }

    private static string CreateHelperConfig(string registry) =>
        JsonSerializer.Serialize(new { credHelpers = new Dictionary<string, string> { [registry] = "test" } });

    private static void AssertProtocolCredentials(DockerCredentials credentials, string expectedInput)
    {
        Assert.Equal(
            Encoding.UTF8.GetBytes(expectedInput + Environment.NewLine),
            Convert.FromBase64String(credentials.Username));
        Assert.Equal(
            "fixture-\u00e4-\U0001F512:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(expectedInput + Environment.NewLine)),
            credentials.Password);
        Assert.Null(credentials.IdentityToken);
    }

    private sealed class LookupFixture : IProcessService, IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _scenario;
        private readonly bool _gated;
        private readonly IEnvironment _environment;
        private readonly ProcessObservation _observation = new();
        private readonly CancellationTokenSource _cleanupCancellation = new();
        private Task<ProcessResult>? _runTask;
        private Task<DockerCredentials>? _lookupTask;

        public LookupFixture(
            string config,
            bool containersConfig = false,
            string scenario = "protocol",
            bool gated = false)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"credential tests {Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _scenario = scenario;
            _gated = gated;
            string configPath = Path.Combine(_directory, containersConfig ? "auth.json" : "config.json");
            File.WriteAllText(configPath, config);

            Mock<IEnvironment> environment = new();
            environment.Setup(value => value.GetFolderPath(It.IsAny<Environment.SpecialFolder>()))
                .Returns(_directory);
            environment.Setup(value => value.GetEnvironmentVariable("PATH")).Returns(AppContext.BaseDirectory);
            environment.Setup(value => value.GetEnvironmentVariable("PATHEXT")).Returns(".exe");
            environment.Setup(value => value.GetEnvironmentVariable(
                containersConfig ? "REGISTRY_AUTH_FILE" : "DOCKER_CONFIG"))
                .Returns(containersConfig ? configPath : _directory);
            _environment = environment.Object;
        }

        public Task<DockerCredentials> GetCredentialsAsync(
            string registry,
            CancellationToken cancellationToken = default)
        {
            Assert.Null(_lookupTask);
            _lookupTask = GetCredentialsCoreAsync(registry, cancellationToken);
            return _lookupTask;
        }

        private async Task<DockerCredentials> GetCredentialsCoreAsync(
            string registry,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _cleanupCancellation.Token);
            return await CredsProvider.GetCredentialsAsync(
                registry, new FileSystem(), this, _environment, linked.Token);
        }

        public Task<ProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            string? input,
            int maxOutputBytesPerStream,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Assert.Null(_runTask);
            Assert.Equal("get", startInfo.Arguments);
            Assert.True(startInfo.CreateNoWindow);
            Assert.False(startInfo.UseShellExecute);
            Assert.Equal(TimeSpan.FromSeconds(30), timeout);
            Assert.Equal(1024 * 1024, maxOutputBytesPerStream);
            startInfo.Environment["DOCKER_CREDS_TEST_SCENARIO"] = _scenario;
            startInfo.Environment.Remove("DOCKER_CREDS_TEST_READY");
            startInfo.Environment.Remove("DOCKER_CREDS_TEST_RELEASE");
            if (_gated)
            {
                startInfo.Environment["DOCKER_CREDS_TEST_READY"] = Path.Combine(_directory, "ready");
                startInfo.Environment["DOCKER_CREDS_TEST_RELEASE"] = Path.Combine(_directory, "release");
            }

            _runTask = new ProcessService(_observation.Capture).RunAsync(
                startInfo, input, maxOutputBytesPerStream, timeout, cancellationToken);
            return _runTask;
        }

        public async Task WaitUntilReadyAsync()
        {
            using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(_directory, "ready")))
            {
                Assert.NotNull(_lookupTask);
                if (_lookupTask.IsCompleted)
                {
                    await _lookupTask;
                    Assert.Fail("Credential helper exited before signaling readiness.");
                }
                await Task.Delay(10, watchdog.Token);
            }
        }

        public void Release() => File.WriteAllText(Path.Combine(_directory, "release"), "release");

        public void AssertExitedAndDisposed() => _observation.AssertExitedAndDisposed();

        public async ValueTask DisposeAsync()
        {
            _cleanupCancellation.Cancel();
            try
            {
                if (_lookupTask is not null)
                {
                    await Task.WhenAny(_lookupTask, Task.Delay(TimeSpan.FromSeconds(5)));
                    Assert.True(_lookupTask.IsCompleted, "Credential lookup cleanup did not complete.");
                    // The test owns result assertions; observe expected failures again during cleanup.
                    _ = _lookupTask.Exception;
                }
            }
            finally
            {
                _observation.Dispose();
                _cleanupCancellation.Dispose();
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
