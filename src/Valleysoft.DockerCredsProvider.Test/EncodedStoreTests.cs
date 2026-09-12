using Moq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class EncodedStoreTests
{
    private readonly IEnvironment _defaultEnvironmentMock = new Mock<IEnvironment>().WithTestEnvironment().Object;

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"auth\": null }")]
    [InlineData("{ \"identitytoken\": \"token-without-auth\" }")]
    public async Task MissingAuthThrowsJsonExceptionWithRegistryAndConfigContext(string entry)
    {
        JsonException exception = await Assert.ThrowsAsync<JsonException>(() => ResolveEntryAsync(entry));

        Assert.Contains("auth", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry.example.com", exception.Message);
        Assert.Contains(Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json"), exception.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("\"entry\"")]
    [InlineData("{ \"auth\": 1 }")]
    [InlineData("{ \"auth\": true }")]
    [InlineData("{ \"auth\": [] }")]
    [InlineData("{ \"auth\": {} }")]
    [InlineData("{ \"auth\": \"dXNlcjpwYXNzd29yZA==\", \"identitytoken\": 1 }")]
    [InlineData("{ \"auth\": \"dXNlcjpwYXNzd29yZA==\", \"identitytoken\": true }")]
    [InlineData("{ \"auth\": \"dXNlcjpwYXNzd29yZA==\", \"identitytoken\": [] }")]
    [InlineData("{ \"auth\": \"dXNlcjpwYXNzd29yZA==\", \"identitytoken\": {} }")]
    public async Task WrongCredentialJsonTypeThrowsInvalidOperationException(string entry)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveEntryAsync(entry));
    }

    [Theory]
    [InlineData("@")]
    [InlineData("a")]
    [InlineData("====")]
    [InlineData("dXNlcjpwYXNz*")]
    public async Task InvalidBase64ThrowsFormatException(string auth)
    {
        string entry = JsonSerializer.Serialize(new { auth });

        await Assert.ThrowsAsync<FormatException>(() => ResolveEntryAsync(entry));
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-separator")]
    [InlineData(":password")]
    [InlineData("username:")]
    public async Task InvalidDecodedCredentialsHaveRegistryAndConfigContext(string decoded)
    {
        string entry = JsonSerializer.Serialize(new
        {
            auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded))
        });

        JsonException exception = await Assert.ThrowsAsync<JsonException>(() => ResolveEntryAsync(entry));

        Assert.Contains("registry.example.com", exception.Message);
        Assert.Contains(Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json"), exception.Message);
    }

    [Theory]
    [InlineData("testuser", "<CREDENTIAL_PLACEHOLDER>")]
    [InlineData("username", "password:with:colons")]
    [InlineData("username", ":leading-and-trailing:")]
    [InlineData("us\u00e9r", "p\u00e4ss:\u79d8\u5bc6")]
    public async Task PasswordRetainsItsValue(string username, string password)
    {
        string entry = JsonSerializer.Serialize(new
        {
            auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))
        });

        DockerCredentials credentials = await ResolveEntryAsync(entry);

        Assert.Equal(username, credentials.Username);
        Assert.Equal(password, credentials.Password);
        Assert.Null(credentials.IdentityToken);
    }

    [Fact]
    public async Task NullIdentityTokenDoesNotDiscardPassword()
    {
        DockerCredentials credentials = await ResolveEntryAsync(
            "{ \"auth\": \"dXNlcjpwYXNzd29yZA==\", \"identitytoken\": null }");

        Assert.Equal("user", credentials.Username);
        Assert.Equal("password", credentials.Password);
        Assert.Null(credentials.IdentityToken);
    }

    [Theory]
    [InlineData("<token>", "<token>", "")]
    [InlineData("<token>", "<token>", "token:\u79d8\u5bc6")]
    [InlineData("<token>", "<token>", "tokenstring")]
    [InlineData("00000000-0000-0000-0000-000000000000:", "00000000-0000-0000-0000-000000000000", "tokenstring")]
    public async Task IdentityTokenRetainsItsValue(string decoded, string expectedUsername, string identityToken)
    {
        string entry = JsonSerializer.Serialize(new
        {
            auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded)),
            identitytoken = identityToken
        });

        DockerCredentials credentials = await ResolveEntryAsync(entry);

        Assert.Equal(expectedUsername, credentials.Username);
        Assert.Null(credentials.Password);
        Assert.Equal(identityToken, credentials.IdentityToken);
    }

    [Fact]
    public async Task EncodedStore_NoMatch()
    {
        string entry = JsonSerializer.Serialize(new
        {
            auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:<CREDENTIAL_PLACEHOLDER>"))
        });

        await Assert.ThrowsAsync<CredsNotFoundException>(() => ResolveEntryAsync(entry, "other.example.com"));
    }

    private Task<DockerCredentials> ResolveEntryAsync(string entry, string requestedRegistry = "registry.example.com")
    {
        string path = Path.Combine(MockExtensions.TestProfileDirectory, ".docker", "config.json");
        Mock<IFileSystem> fileSystem = new();
        fileSystem.WithFile(path, $"{{ \"auths\": {{ \"registry.example.com\": {entry} }} }}");
        return CredsProvider.GetCredentialsAsync(
            requestedRegistry,
            fileSystem.Object,
            Mock.Of<IProcessService>(MockBehavior.Strict),
            _defaultEnvironmentMock);
    }
}
