using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Valleysoft.DockerCredsProvider.Test;

public class ProcessServiceTests
{
    [Fact]
    public async Task RunAsync_SuccessfulCompletion()
    {
        ProcessStartInfo startInfo = CreateCommand("echo success");
        List<string> output = new();

        int exitCode = await new ProcessService().RunAsync(
            startInfo,
            input: null,
            value =>
            {
                if (value is not null)
                {
                    output.Add(value);
                }
            },
            _ => { },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("success", output);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationTerminatesProcess()
    {
        ProcessStartInfo startInfo = CreateSleepCommand(TimeSpan.FromSeconds(30));
        using CancellationTokenSource cancellationSource = new(TimeSpan.FromMilliseconds(200));
        int processId = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessService(process => processId = process.Id).RunAsync(
                startInfo,
                input: null,
                _ => { },
                _ => { },
                TimeSpan.FromSeconds(30),
                cancellationSource.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        AssertProcessExited(processId);
    }

    [Fact]
    public async Task RunAsync_TimeoutTerminatesProcess()
    {
        ProcessStartInfo startInfo = CreateSleepCommand(TimeSpan.FromSeconds(30));
        int processId = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessService(process => processId = process.Id).RunAsync(
                startInfo,
                input: null,
                _ => { },
                _ => { },
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        AssertProcessExited(processId);
    }

    [Fact]
    public async Task RunAsync_CompletionNearDeadlineDoesNotTimeOut()
    {
        ProcessStartInfo startInfo = CreateSleepCommand(TimeSpan.FromMilliseconds(1250));

        int exitCode = await new ProcessService().RunAsync(
            startInfo,
            input: null,
            _ => { },
            _ => { },
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_TimeoutIncludesBlockedInputWrite()
    {
        ProcessStartInfo startInfo = CreateSleepCommand(TimeSpan.FromSeconds(30));
        startInfo.RedirectStandardInput = true;
        string input = new('x', 1024 * 1024);
        int processId = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessService(process => processId = process.Id).RunAsync(
                startInfo,
                input,
                _ => { },
                _ => { },
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        AssertProcessExited(processId);
    }

    private static ProcessStartInfo CreateCommand(string command)
    {
        ProcessStartInfo startInfo = CreateStartInfo();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateSleepCommand(TimeSpan duration)
    {
        ProcessStartInfo startInfo = CreateStartInfo();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            int pingCount = Math.Max(2, (int)Math.Ceiling(duration.TotalSeconds) + 1);
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"ping 127.0.0.1 -n {pingCount} > nul");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"sleep {duration.TotalSeconds}");
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateStartInfo() =>
        new()
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

    private static void AssertProcessExited(int processId)
    {
        Assert.NotEqual(0, processId);

        try
        {
            using Process process = Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }
}
