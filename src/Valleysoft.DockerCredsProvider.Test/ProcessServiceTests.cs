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
    public async Task RunAsync_DrainsBothStreamsBeforeHelperReadsInput()
    {
        using ProcessObservation observation = new();
        string payload = new('x', OutputLimit);

        ProcessResult result = await new ProcessService(observation.Capture).RunAsync(
            CreateHelperCommand("pipe-pressure", OutputLimit.ToString(CultureInfo.InvariantCulture)),
            payload,
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(payload, result.StandardOutput);
        Assert.Equal(payload, result.StandardError);
        observation.AssertExitedAndDisposed();
    }

    [Theory]
    [InlineData("stdout", "standard output")]
    [InlineData("stderr", "standard error")]
    public async Task RunAsync_OutputOverflowTerminatesHelperWithBlockedInput(
        string fixtureStream,
        string expectedStreamName)
    {
        using ProcessObservation observation = new();
        Task<ProcessResult> runTask = new ProcessService(observation.Capture).RunAsync(
            CreateHelperCommand(
                "overflow-blocked-input",
                fixtureStream,
                OutputLimit.ToString(CultureInfo.InvariantCulture)),
            new string('x', OutputLimit),
            OutputLimit,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Same(runTask, await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5))));
        ProcessOutputLimitExceededException exception =
            await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() => runTask);

        Assert.Equal(expectedStreamName, exception.StreamName);
        Assert.Equal(OutputLimit, exception.Limit);
        observation.AssertExitedAndDisposed();
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

    [Theory]
    [InlineData("success", 0, 20)]
    [InlineData("success", 0, OutputLimit)]
    [InlineData("failure", 17, 20)]
    [InlineData("failure", 17, OutputLimit)]
    public async Task RunAsync_ClosedInputWaitsForNaturalExit(string outcome, int exitCode, int inputLength)
    {
        string releasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.release");
        using ProcessObservation observation = new();
        int terminationAttempts = 0;
        try
        {
            Task<ProcessResult> runTask = new ProcessService(
                process => ObserveClosedInputHelper(process, observation),
                process =>
                {
                    terminationAttempts++;
                    process.Kill(entireProcessTree: true);
                    return true;
                }).RunAsync(
                    CreateHelperCommand("closed-input", releasePath, outcome),
                    new string('x', inputLength),
                    OutputLimit,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None);

            // Hold the helper alive after closing stdin, rather than relying on exit/pipe scheduling.
            Task observationWindow = Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.Same(observationWindow, await Task.WhenAny(runTask, observationWindow));
            File.WriteAllText(releasePath, "release");

            ProcessResult result = await runTask;
            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal("fixture-stdout-secret", result.StandardOutput);
            Assert.Equal("fixture-stderr-secret", result.StandardError);
            Assert.Equal(0, terminationAttempts);
            observation.AssertExitedAndDisposed();
        }
        finally
        {
            File.Delete(releasePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ClosedInputDoesNotPreventTimeoutOrCancellation(bool cancel)
    {
        string releasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.release");
        using ProcessObservation observation = new();
        using CancellationTokenSource cancellationSource = new();
        Task<ProcessResult> runTask = new ProcessService(process =>
        {
            ObserveClosedInputHelper(process, observation);
            if (cancel)
            {
                cancellationSource.CancelAfter(TimeSpan.FromMilliseconds(250));
            }
        }).RunAsync(
            CreateHelperCommand("closed-input", releasePath, "failure"),
            new string('x', OutputLimit),
            OutputLimit,
            cancel ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(250),
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
    }

    [Theory]
    [InlineData("stdout", "standard output")]
    [InlineData("stderr", "standard error")]
    public async Task RunAsync_ClosedInputDoesNotHideOutputOverflow(string streamName, string expectedStreamName)
    {
        string releasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.release");
        using ProcessObservation observation = new();
        try
        {
            Task<ProcessResult> runTask = new ProcessService(
                process => ObserveClosedInputHelper(process, observation)).RunAsync(
                    CreateHelperCommand("closed-input", releasePath, streamName),
                    new string('x', OutputLimit),
                    OutputLimit,
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);
            File.WriteAllText(releasePath, "release");

            Assert.Same(runTask, await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5))));
            ProcessOutputLimitExceededException exception =
                await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() => runTask);
            Assert.Equal(expectedStreamName, exception.StreamName);
            Assert.Equal(OutputLimit, exception.Limit);
            observation.AssertExitedAndDisposed();
        }
        finally
        {
            File.Delete(releasePath);
        }
    }

    private static void ObserveClosedInputHelper(Process process, ProcessObservation observation)
    {
        observation.Capture(process);
        Assert.Equal("ready", process.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        Assert.False(process.HasExited);
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
    public async Task RunAsync_TerminatesLiveParentAndDescendant(bool cancel)
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
                Assert.Equal("ready", process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                child = Process.GetProcessById(int.Parse(
                    File.ReadAllText(childProcessIdPath), CultureInfo.InvariantCulture));
                _ = child.SafeHandle;
                Assert.False(process.HasExited);
                Assert.False(child.HasExited);
                if (cancel)
                {
                    cancellationSource.Cancel();
                }
            }).RunAsync(
                CreateHelperCommand("spawn-child", childProcessIdPath),
                input: null,
                OutputLimit,
                cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(250),
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
            Assert.True(child.WaitForExit(5000));
        }
        finally
        {
            CleanupChild(child, childProcessIdPath);
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
            CleanupChild(child, childProcessIdPath);
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

    [Theory]
    [InlineData("stdout", OutputLimit - 1)]
    [InlineData("stdout", OutputLimit)]
    [InlineData("stderr", OutputLimit - 1)]
    [InlineData("stderr", OutputLimit)]
    public async Task RunAsync_AllowsOutputUpToLimit(string fixtureStream, int byteCount)
    {
        ProcessStartInfo startInfo = CreateHelperCommand(
            "output",
            fixtureStream,
            byteCount.ToString(CultureInfo.InvariantCulture));
        using ProcessObservation observation = new();

        ProcessResult result = await new ProcessService(observation.Capture).RunAsync(
            startInfo,
            input: null,
            OutputLimit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(fixtureStream == "stdout" ? byteCount : 0, result.StandardOutput.Length);
        Assert.Equal(fixtureStream == "stderr" ? byteCount : 0, result.StandardError.Length);
        observation.AssertExitedAndDisposed();
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

    [Theory]
    [InlineData("stdout", false, false)]
    [InlineData("stdout", false, true)]
    [InlineData("stdout", true, false)]
    [InlineData("stdout", true, true)]
    [InlineData("stderr", false, false)]
    [InlineData("stderr", false, true)]
    [InlineData("stderr", true, false)]
    [InlineData("stderr", true, true)]
    public async Task RunAsync_EnforcesUtf8ByteLimitIncludingBom(
        string fixtureStream,
        bool includeBom,
        bool exceedsLimit)
    {
        const string expectedOutput = "\u00fcser-\U0001F512\uFEFF";
        string wireOutput = (includeBom ? "\uFEFF" : string.Empty) + expectedOutput;
        int limit = Encoding.UTF8.GetByteCount(wireOutput) - (exceedsLimit ? 1 : 0);
        ProcessStartInfo startInfo = CreateHelperCommand("utf8-output", fixtureStream, wireOutput);
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        using ProcessObservation observation = new();

        Assert.True(wireOutput.Length < limit);
        Task<ProcessResult> runTask = new ProcessService(observation.Capture).RunAsync(
            startInfo,
            input: null,
            limit,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        if (exceedsLimit)
        {
            ProcessOutputLimitExceededException exception =
                await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() => runTask);
            Assert.Equal(fixtureStream == "stdout" ? "standard output" : "standard error", exception.StreamName);
            Assert.Equal(limit, exception.Limit);
        }
        else
        {
            ProcessResult result = await runTask;
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(fixtureStream == "stdout" ? expectedOutput : string.Empty, result.StandardOutput);
            Assert.Equal(fixtureStream == "stderr" ? expectedOutput : string.Empty, result.StandardError);
        }

        observation.AssertExitedAndDisposed();
    }

    private static void CleanupChild(Process? child, string childProcessIdPath)
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

}
