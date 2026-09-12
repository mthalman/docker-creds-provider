using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class CredsProviderTests
{
    [Fact]
    public async Task GetCredentialsAsync_CanceledBeforeLookup()
    {
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CredsProvider.GetCredentialsAsync("test", cancellationSource.Token));
    }

    [Fact]
    public Task NullRegistry()
    {
        return Assert.ThrowsAsync<ArgumentNullException>(() => CredsProvider.GetCredentialsAsync(null!));
    }
}
