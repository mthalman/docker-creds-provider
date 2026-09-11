using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class ProcessServiceTests
{
    private const int OutputLimit = 1024 * 1024;

    [Fact]
    public async Task RunAsync_SuccessfullyDrainsOutput()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("success");
        using ProcessObservation observation = new();
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;

        ProcessResult result = await new ProcessService(process =>
        {
            observation.Capture(process);
            standardOutput = process.StandardOutput;
            standardError = process.StandardError;
        }).RunAsync(
            startInfo,
            "registry.example.com",
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "{\"Username\":\"fixture-user\",\"Secret\":\"fixture-secret\",\"ServerURL\":\"registry.example.com\"}",
            result.StandardOutput);
        Assert.Empty(result.StandardError);
        observation.AssertExitedAndDisposed();
        AssertReaderDisposed(standardOutput);
        AssertReaderDisposed(standardError);
    }

    [Fact]
    public async Task RunAsync_WritesInputAsUtf8()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("get");
        startInfo.StandardInputEncoding = Encoding.ASCII;

        ProcessResult result = await new ProcessService().RunAsync(
            startInfo,
            "unicode-registry-例",
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"Username\":\"fixture-user\"", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public async Task RunAsync_CompletesWithOptionalRedirection(
        bool redirectInput,
        bool redirectOutput,
        bool redirectError)
    {
        ProcessStartInfo startInfo = CreateHelperCommand("output", "stdout", "0");
        startInfo.RedirectStandardInput = redirectInput;
        startInfo.RedirectStandardOutput = redirectOutput;
        startInfo.RedirectStandardError = redirectError;
        using ProcessObservation observation = new();

        ProcessResult result = await new ProcessService(observation.Capture).RunAsync(
            startInfo,
            input: null,
            maxOutputBytesPerStream: 0,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        observation.AssertExitedAndDisposed();
    }

    [Fact]
    public async Task RunAsync_DoesNotWriteConfiguredInputEncodingPreamble()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("get");
        startInfo.StandardInputEncoding = Encoding.Unicode;

        ProcessResult result = await new ProcessService().RunAsync(
            startInfo,
            "password",
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"Username\":\"fixture-user\"", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RunAsync_PropertyAbsentFallbackPreservesConsoleEncoding()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("get");
        Encoding originalEncoding = Console.InputEncoding;

        ProcessResult result = await new ProcessService(
            forceStandardInputEncodingFallback: true).RunAsync(
                startInfo,
                "password",
                OutputLimit,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"Username\":\"fixture-user\"", result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(originalEncoding.CodePage, Console.InputEncoding.CodePage);
        Assert.Equal(originalEncoding.GetPreamble(), Console.InputEncoding.GetPreamble());
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureExitCodeAndDrainedOutput()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("failure");
        using ProcessObservation observation = new();
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;

        ProcessResult result = await new ProcessService(process =>
        {
            observation.Capture(process);
            standardOutput = process.StandardOutput;
            standardError = process.StandardError;
        }).RunAsync(
            startInfo,
            input: null,
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(17, result.ExitCode);
        Assert.Equal("fixture-stdout-secret", result.StandardOutput);
        Assert.Equal("fixture-stderr-secret", result.StandardError);
        observation.AssertExitedAndDisposed();
        AssertReaderDisposed(standardOutput);
        AssertReaderDisposed(standardError);
    }

    [Fact]
    public async Task RunAsync_ProcessExitSupersedesClosedInputPipe()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("failure");

        ProcessResult result = await new ProcessService().RunAsync(
            startInfo,
            new string('x', OutputLimit),
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(17, result.ExitCode);
        Assert.Equal("fixture-stdout-secret", result.StandardOutput);
        Assert.Equal("fixture-stderr-secret", result.StandardError);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(OutputLimit)]
    public async Task RunAsync_ExitedHelperPreservesResultWhenClosingInputFails(int inputLength)
    {
        ProcessStartInfo startInfo = CreateHelperCommand("failure");
        using ProcessObservation observation = new();

        ProcessResult result = await new ProcessService(process =>
        {
            observation.Capture(process);
            Assert.True(process.WaitForExit(5000));
        }).RunAsync(
            startInfo,
            new string('x', inputLength),
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(17, result.ExitCode);
        Assert.Equal("fixture-stdout-secret", result.StandardOutput);
        Assert.Equal("fixture-stderr-secret", result.StandardError);
        observation.AssertExitedAndDisposed();
    }

    [Fact]
    public async Task RunAsync_CallerCancellationTerminatesAndDisposesProcess()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("wait");
        using CancellationTokenSource cancellationSource = new(TimeSpan.FromMilliseconds(200));
        using ProcessObservation observation = new();
        Stopwatch stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessService(observation.Capture).RunAsync(
                startInfo,
                input: null,
                OutputLimit,
                TimeSpan.FromSeconds(30),
                cancellationSource.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        observation.AssertExitedAndDisposed();
    }

    [Fact]
    public async Task RunAsync_TimeoutTerminatesAndDisposesProcess()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("wait");
        using ProcessObservation observation = new();
        Stopwatch stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessService(observation.Capture).RunAsync(
                startInfo,
                input: null,
                OutputLimit,
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        observation.AssertExitedAndDisposed();
    }

    [Fact]
    public async Task RunAsync_TerminationFailureDoesNotReplaceCancellation()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("wait");
        using CancellationTokenSource cancellationSource = new(TimeSpan.FromMilliseconds(200));
        Process? startedProcess = null;
        Process? cleanupProcess = null;
        int attempts = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ProcessService(
                    process =>
                    {
                        startedProcess = process;
                        cleanupProcess = Process.GetProcessById(process.Id);
                    },
                    _ =>
                    {
                        attempts++;
                        throw new Win32Exception("Simulated termination failure.");
                    })
                .RunAsync(
                    startInfo,
                    new string('x', OutputLimit),
                    OutputLimit,
                    TimeSpan.FromSeconds(30),
                    cancellationSource.Token));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(1, attempts);
            Assert.NotNull(startedProcess);
            Assert.Throws<InvalidOperationException>(() => _ = startedProcess.SafeHandle);
            Assert.Equal(
                "Credential helper termination could not be confirmed; the process may still be running.",
                exception.Data["CredentialHelperCleanup"]);
        }
        finally
        {
            if (cleanupProcess is not null)
            {
                cleanupProcess.Kill(entireProcessTree: true);
                cleanupProcess.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RunAsync_UnconfirmedTerminationPreservesOriginalFailure(bool cancel, bool terminationThrows)
    {
        using CancellationTokenSource cancellationSource = new();
        using ProcessObservation observation = new();
        int attempts = 0;
        Task runTask = new ProcessService(
            process =>
            {
                observation.Capture(process);
                if (cancel)
                {
                    cancellationSource.Cancel();
                }
            },
            process =>
            {
                attempts++;
                if (terminationThrows)
                {
                    throw new Win32Exception("Termination failure.");
                }

                return true;
            }).RunAsync(
                CreateHelperCommand("wait"),
                new string('x', OutputLimit),
                OutputLimit,
                cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(200),
                cancellationSource.Token);

        Assert.Same(runTask, await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5))));
        Exception exception = cancel
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask)
            : await Assert.ThrowsAsync<TimeoutException>(() => runTask);

        Assert.Equal(1, attempts);
        Assert.Equal(
            "Credential helper termination could not be confirmed; the process may still be running.",
            exception.Data["CredentialHelperCleanup"]);
        observation.AssertRunningAndDisposed();
    }

    [Fact]
    public async Task RunAsync_StreamFailureRetainsCleanupDiagnostic()
    {
        using ProcessObservation observation = new();
        int attempts = 0;
        ProcessStreamException exception = await Assert.ThrowsAsync<ProcessStreamException>(() =>
            new ProcessService(
                process =>
                {
                    observation.Capture(process);
                    process.StandardOutput.Dispose();
                },
                _ =>
                {
                    attempts++;
                    throw new Win32Exception("Termination failure.");
                }).RunAsync(
                    CreateHelperCommand("wait"),
                    input: null,
                    OutputLimit,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal("standard output", exception.StreamName);
        Assert.Null(exception.ExitCode);
        ProcessStreamException original = Assert.IsType<ProcessStreamException>(exception.InnerException);
        Assert.Equal(
            "Credential helper termination could not be confirmed; the process may still be running.",
            original.Data["CredentialHelperCleanup"]);
        observation.AssertRunningAndDisposed();
    }

    [Fact]
    public async Task RunAsync_UnconfirmedTerminationDoesNotExtendTimeoutIndefinitely()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("wait");
        Process? cleanupProcess = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        Task runTask = new ProcessService(
            process => cleanupProcess = Process.GetProcessById(process.Id),
            _ => true).RunAsync(
                startInfo,
                input: null,
                OutputLimit,
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None);

        try
        {
            Task completedTask = await Task.WhenAny(
                runTask,
                Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(runTask, completedTask);
            TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => runTask);
            Assert.Equal(
                "Credential helper termination could not be confirmed; the process may still be running.",
                exception.Data["CredentialHelperCleanup"]);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (cleanupProcess is not null)
            {
                cleanupProcess.Kill(entireProcessTree: true);
                cleanupProcess.Dispose();
            }
        }
    }

    [Fact]
    public async Task RunAsync_SingleProcessTerminationDoesNotWaitForDescendantPipe()
    {
        string childProcessIdPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.pid");
        ProcessStartInfo startInfo = CreateHelperCommand(
            "spawn-child",
            childProcessIdPath);
        Task runTask = new ProcessService(
            _ => { },
            process =>
            {
                process.Kill();
                return true;
            }).RunAsync(
                startInfo,
                input: null,
                OutputLimit,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        try
        {
            Task completedTask = await Task.WhenAny(
                runTask,
                Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(runTask, completedTask);
            await Assert.ThrowsAsync<TimeoutException>(() => runTask);
        }
        finally
        {
            if (File.Exists(childProcessIdPath))
            {
                int childProcessId = int.Parse(
                    File.ReadAllText(childProcessIdPath),
                    CultureInfo.InvariantCulture);
                using Process childProcess = Process.GetProcessById(childProcessId);
                childProcess.Kill(entireProcessTree: true);
                File.Delete(childProcessIdPath);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ExitedParentDoesNotWaitForSurvivingDescendant(bool cancel)
    {
        string childProcessIdPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pid");
        using ProcessObservation observation = new();
        using CancellationTokenSource cancellationSource = new();
        Process? child = null;
        try
        {
            Task runTask = new ProcessService(process =>
            {
                observation.Capture(process);
                Assert.True(process.WaitForExit(5000));
                Assert.Equal(0, process.ExitCode);
                child = Process.GetProcessById(int.Parse(
                    File.ReadAllText(childProcessIdPath), CultureInfo.InvariantCulture));
                _ = child.SafeHandle;
                if (cancel)
                {
                    cancellationSource.Cancel();
                }
            }).RunAsync(
                CreateHelperCommand("spawn-child-and-exit", childProcessIdPath),
                input: null,
                OutputLimit,
                TimeSpan.FromMilliseconds(250),
                cancellationSource.Token);

            Assert.Same(runTask, await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5))));
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => runTask);
            }

            observation.AssertExitedAndDisposed();
            Assert.NotNull(child);
            Assert.False(child.HasExited);
        }
        finally
        {
            if (child is null && File.Exists(childProcessIdPath))
            {
                child = Process.GetProcessById(int.Parse(
                    File.ReadAllText(childProcessIdPath), CultureInfo.InvariantCulture));
            }
            using (child)
            {
                if (child is not null && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    Assert.True(child.WaitForExit(5000));
                }
            }
            File.Delete(childProcessIdPath);
        }
    }

    [Theory]
    [InlineData("stdout", "standard output")]
    [InlineData("stderr", "standard error")]
    public async Task RunAsync_OutputLimitTerminatesAndDisposesProcess(
        string fixtureStream,
        string expectedStreamName)
    {
        ProcessStartInfo startInfo = CreateHelperCommand(
            "output",
            fixtureStream,
            (OutputLimit + 1).ToString());
        using ProcessObservation observation = new();

        ProcessOutputLimitExceededException exception =
            await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
                new ProcessService(observation.Capture).RunAsync(
                    startInfo,
                    input: null,
                    OutputLimit,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));

        Assert.Equal(expectedStreamName, exception.StreamName);
        Assert.Equal(OutputLimit, exception.Limit);
        observation.AssertExitedAndDisposed();
    }

    [Fact]
    public async Task RunAsync_AllowsOutputAtLimit()
    {
        ProcessStartInfo startInfo = CreateHelperCommand(
            "output",
            "stdout",
            OutputLimit.ToString());

        ProcessResult result = await new ProcessService().RunAsync(
            startInfo,
            input: null,
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(OutputLimit, result.StandardOutput.Length);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RunAsync_TimeoutIncludesBlockedInputWrite()
    {
        ProcessStartInfo startInfo = CreateHelperCommand("wait");
        string input = new('x', OutputLimit);
        using ProcessObservation observation = new();
        Stopwatch stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessService(observation.Capture).RunAsync(
                startInfo,
                input,
                OutputLimit,
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        observation.AssertExitedAndDisposed();
    }

    private static ProcessStartInfo CreateHelperCommand(params string[] arguments)
    {
        string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "docker-credential-test.exe"
            : "docker-credential-test";
        ProcessStartInfo startInfo = new(Path.Combine(AppContext.BaseDirectory, executableName))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void AssertReaderDisposed(StreamReader? reader)
    {
        Assert.NotNull(reader);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    private sealed class ProcessObservation : IDisposable
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
}
