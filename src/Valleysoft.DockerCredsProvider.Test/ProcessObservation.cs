using System.Diagnostics;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

internal sealed class ProcessObservation : IDisposable
{
    private Process? _original;
    private Process? _independent;

    public void Capture(Process process)
    {
        _original = process;
        _independent = Process.GetProcessById(process.Id);
        // Open a handle before the helper can exit, avoiding PID reuse during assertions.
        _ = _independent.SafeHandle;
    }

    public void AssertExitedAndDisposed()
    {
        Assert.NotNull(_independent);
        Assert.True(_independent.WaitForExit(1000));
        Assert.NotNull(_original);
        Assert.Throws<InvalidOperationException>(() => _ = _original.SafeHandle);
    }

    public void AssertRunningAndDisposed()
    {
        Assert.NotNull(_independent);
        Assert.False(_independent.HasExited);
        Assert.NotNull(_original);
        Assert.Throws<InvalidOperationException>(() => _ = _original.SafeHandle);
    }

    public void Dispose()
    {
        if (_independent is not null)
        {
            try
            {
                if (!_independent.HasExited)
                {
                    _independent.Kill(entireProcessTree: true);
                    Assert.True(_independent.WaitForExit(5000));
                }
            }
            finally
            {
                _independent.Dispose();
            }
        }
    }
}
