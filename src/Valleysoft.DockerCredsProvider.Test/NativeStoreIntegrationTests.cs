using System.Runtime.InteropServices;
using System.Text;
using Moq;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class NativeStoreIntegrationTests
{
    [Theory]
    [InlineData("password", "fixture-user", "fixture-password")]
    [InlineData("unicode", "fixture-üser", "pässword-🔒")]
    [InlineData("unicode-registry-例", "fixture-user", "fixture-password")]
    [InlineData("empty-secret", "fixture-user", "")]
    [InlineData("extra-fields", "fixture-user", "fixture-password")]
    public async Task GetCredentialsAsync_ReturnsUsernameAndPassword(
        string registry,
        string expectedUsername,
        string expectedPassword)
    {
        NativeStore store = CreateStore();

        DockerCredentials credentials = await store.GetCredentialsAsync(
            registry,
            CancellationToken.None);

        Assert.Equal(expectedUsername, credentials.Username);
        Assert.Equal(expectedPassword, credentials.Password);
        Assert.Null(credentials.IdentityToken);
    }

    [Fact]
    public async Task GetCredentialsAsync_ReturnsIdentityToken()
    {
        NativeStore store = CreateStore();

        DockerCredentials credentials = await store.GetCredentialsAsync(
            "token",
            CancellationToken.None);

        Assert.Equal("<token>", credentials.Username);
        Assert.Null(credentials.Password);
        Assert.Equal("fixture-token", credentials.IdentityToken);
    }

    [Theory]
    [InlineData("missing-username", "Username")]
    [InlineData("missing-secret", "Secret")]
    [InlineData("null-username", "Username")]
    [InlineData("null-secret", "Secret")]
    [InlineData("numeric-username", "Username")]
    [InlineData("object-secret", "Secret")]
    public async Task GetCredentialsAsync_RejectsMissingOrInvalidRequiredField(
        string registry,
        string expectedField)
    {
        NativeStore store = CreateStore();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCredentialsAsync(registry, CancellationToken.None));

        Assert.Contains(expectedField, exception.Message);
        Assert.Contains("docker-credential-test", exception.Message);
        Assert.DoesNotContain("fixture-secret", exception.ToString());
    }

    [Theory]
    [InlineData("malformed", "malformed JSON")]
    [InlineData("non-object", "invalid response")]
    public async Task GetCredentialsAsync_RejectsInvalidJson(
        string registry,
        string expectedMessage)
    {
        NativeStore store = CreateStore();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCredentialsAsync(registry, CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.DoesNotContain("fixture-secret", exception.ToString());
    }

    [Fact]
    public async Task GetCredentialsAsync_RejectsHelperFailureWithoutExposingOutput()
    {
        NativeStore store = CreateStore();

        CredsNotFoundException exception = await Assert.ThrowsAsync<CredsNotFoundException>(
            () => store.GetCredentialsAsync("failure", CancellationToken.None));

        Assert.Contains("docker-credential-test", exception.Message);
        Assert.Contains("code 17", exception.Message);
        Assert.DoesNotContain("fixture-stdout-secret", exception.ToString());
        Assert.DoesNotContain("fixture-stderr-secret", exception.ToString());
    }

    [Theory]
    [InlineData("standard output")]
    [InlineData("standard error")]
    public async Task GetCredentialsAsync_StreamFailureIsSanitized(string streamName)
    {
        NativeStore store = CreateStore(
            new ProcessService(process =>
            {
                if (streamName == "standard output")
                {
                    process.StandardOutput.Dispose();
                }
                else
                {
                    process.StandardError.Dispose();
                }
            }));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCredentialsAsync("password", CancellationToken.None));

        Assert.Equal(typeof(InvalidOperationException), exception.GetType());
        Assert.Contains("docker-credential-test", exception.Message);
        Assert.Contains(streamName, exception.Message);
        Assert.Matches(@"exit code -?\d+", exception.Message);
        Assert.DoesNotContain("fixture-password", exception.ToString());

        ProcessStreamException streamException =
            Assert.IsType<ProcessStreamException>(exception.InnerException);
        Assert.Equal(streamName, streamException.StreamName);
        Assert.NotNull(streamException.ExitCode);
    }

    [Fact]
    public async Task GetCredentialsAsync_BomProducingLegacyInputEncodingFailsSafely()
    {
        NativeStore store = CreateStore(
            new ProcessService(
                forceStandardInputEncodingFallback: true,
                standardInputEncodingFallback: Encoding.Unicode));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCredentialsAsync("password", CancellationToken.None));

        Assert.Equal(typeof(InvalidOperationException), exception.GetType());
        Assert.Contains("docker-credential-test", exception.Message);
        Assert.Contains("standard input", exception.Message);
        Assert.Contains("exit code unavailable", exception.Message);
        Assert.DoesNotContain("fixture-password", exception.ToString());
    }

    [Fact]
    public async Task GetCredentialsAsync_LegacyInputEncodingThatChangesAsciiFailsSafely()
    {
#pragma warning disable SYSLIB0001
        Encoding encoding = Encoding.UTF7;
#pragma warning restore SYSLIB0001
        NativeStore store = CreateStore(
            new ProcessService(
                forceStandardInputEncodingFallback: true,
                standardInputEncodingFallback: encoding));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCredentialsAsync("registry+alias", CancellationToken.None));

        Assert.Equal(typeof(InvalidOperationException), exception.GetType());
        Assert.Contains("docker-credential-test", exception.Message);
        Assert.Contains("standard input", exception.Message);
        Assert.Contains("exit code unavailable", exception.Message);
    }

    [Fact]
    public async Task GetCredentialsAsync_LegacyInputEncodingFailureIsSanitized()
    {
        Encoding encoding = Encoding.GetEncoding(
            Encoding.UTF8.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        NativeStore store = CreateStore(
            new ProcessService(
                forceStandardInputEncodingFallback: true,
                standardInputEncodingFallback: encoding));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCredentialsAsync("\ud800", CancellationToken.None));

        Assert.Equal(typeof(InvalidOperationException), exception.GetType());
        Assert.Contains("docker-credential-test", exception.Message);
        Assert.Contains("standard input", exception.Message);
        Assert.Contains("exit code unavailable", exception.Message);
        Assert.IsType<ProcessStreamException>(exception.InnerException);
    }

    private static NativeStore CreateStore(IProcessService? processService = null)
    {
        string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "docker-credential-test.exe"
            : "docker-credential-test";
        string executablePath = Path.Combine(AppContext.BaseDirectory, executableName);

        Mock<IFileSystem> fileSystemMock = new();
        fileSystemMock
            .Setup(fileSystem => fileSystem.FileExists(executablePath))
            .Returns(true);

        Mock<IEnvironment> environmentMock = new();
        environmentMock
            .Setup(environment => environment.GetEnvironmentVariable("PATH"))
            .Returns(AppContext.BaseDirectory);
        environmentMock
            .Setup(environment => environment.GetEnvironmentVariable("PATHEXT"))
            .Returns(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : null);

        return new NativeStore(
            "test",
            processService ?? new ProcessService(),
            fileSystemMock.Object,
            environmentMock.Object);
    }
}
