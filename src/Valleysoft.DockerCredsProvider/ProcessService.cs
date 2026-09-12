using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Valleysoft.DockerCredsProvider;

internal interface IProcessService
{
    Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string? input,
        int maxOutputBytesPerStream,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ProcessResult
{
    public ProcessResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }
}

internal sealed class ProcessOutputLimitExceededException : InvalidOperationException
{
    public ProcessOutputLimitExceededException(string streamName, int limit)
        : base($"Process {streamName} exceeded the configured limit of {limit} bytes.")
    {
        StreamName = streamName;
        Limit = limit;
    }

    public string StreamName { get; }

    public int Limit { get; }
}

internal sealed class ProcessStreamException : IOException
{
    public ProcessStreamException(string streamName, int? exitCode, Exception innerException)
        : base(
            exitCode is null
                ? $"Process {streamName} failed before an exit code was available."
                : $"Process {streamName} failed; process exited with code {exitCode}.",
            innerException)
    {
        StreamName = streamName;
        ExitCode = exitCode;
    }

    public string StreamName { get; }

    public int? ExitCode { get; }
}

internal class ProcessService : IProcessService
{
    private const int StreamBufferSize = 81920;
    private static readonly TimeSpan TerminationWaitTimeout = TimeSpan.FromSeconds(1);

    private readonly Action<Process>? _processStarted;
    private readonly Func<Process, bool>? _terminate;
    private readonly bool _forceStandardInputEncodingFallback;
    private readonly Encoding? _standardInputEncodingFallback;

    public ProcessService()
    {
    }

    internal ProcessService(
        Action<Process> processStarted,
        Func<Process, bool>? terminate = null)
    {
        _processStarted = processStarted;
        _terminate = terminate;
    }

    internal ProcessService(
        bool forceStandardInputEncodingFallback,
        Encoding? standardInputEncodingFallback = null)
    {
        _forceStandardInputEncodingFallback = forceStandardInputEncodingFallback;
        _standardInputEncodingFallback = standardInputEncodingFallback;
    }

    public async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string? input,
        int maxOutputBytesPerStream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (maxOutputBytesPerStream < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytesPerStream));
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();

        ProcessInputEncoding.Configure(
            startInfo, input, _forceStandardInputEncodingFallback, _standardInputEncodingFallback);

        using Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        TaskCompletionSource<bool> exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exitCompletion.TrySetResult(true);

        bool started = false;
        Task inputTask = Task.CompletedTask;
        Task<string> outputTask = Task.FromResult(string.Empty);
        Task<string> errorTask = Task.FromResult(string.Empty);
        List<IDisposable> pipeHandles = new();
        try
        {
            process.Start();
            started = true;
            _processStarted?.Invoke(process);
            CaptureRedirectedPipeHandles(process, pipeHandles);

            inputTask = WriteInputAsync(process, input, exitCompletion.Task);
            outputTask = ReadOutputAsync(
                process,
                standardOutput: true,
                maxOutputBytesPerStream);
            errorTask = ReadOutputAsync(
                process,
                standardOutput: false,
                maxOutputBytesPerStream);

            await WaitForCompletionAsync(
                new[] { exitCompletion.Task, inputTask, outputTask, errorTask },
                Path.GetFileName(startInfo.FileName),
                timeout,
                cancellationToken);

            return await CompleteAsync(
                process,
                inputTask,
                exitCompletion.Task,
                outputTask,
                errorTask);
        }
        catch (Exception e)
        {
            int? exitCode = null;
            if (started)
            {
                exitCode = await CleanupAfterFailureAsync(
                    process,
                    exitCompletion.Task,
                    inputTask,
                    outputTask,
                    errorTask,
                    pipeHandles,
                    e);
            }

            if (e is ProcessStreamException streamFailure)
            {
                throw new ProcessStreamException(streamFailure.StreamName, exitCode, streamFailure);
            }

            throw;
        }
    }

    private static async Task WaitForCompletionAsync(
        Task[] tasks,
        string processName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> cancellationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            () => cancellationCompletion.TrySetResult(true));
        using CancellationTokenSource timeoutCancellationSource = new();
        Task timeoutTask = Task.Delay(timeout, timeoutCancellationSource.Token);
        HashSet<Task> pendingTasks = new(tasks);

        try
        {
            while (pendingTasks.Count > 0)
            {
                Task completedTask = await Task.WhenAny(
                    pendingTasks.Append(timeoutTask).Append(cancellationCompletion.Task));

                // Once everything has completed, let result collection report stream errors in order.
                if (pendingTasks.All(task => task.IsCompleted))
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException(
                        $"Process '{processName}' exceeded the timeout of {timeout}.");
                }

                // Observe failures immediately; Task.WhenAll would wait for possibly blocked sibling I/O.
                await completedTask;
                pendingTasks.Remove(completedTask);
            }
        }
        finally
        {
            timeoutCancellationSource.Cancel();
        }
    }

    private static async Task WriteInputAsync(Process process, string? input, Task exitTask)
    {
        if (!process.StartInfo.RedirectStandardInput)
        {
            if (input is not null)
            {
                throw new InvalidOperationException("Standard input must be redirected when input is provided.");
            }

            return;
        }

        try
        {
            StreamWriter writer = process.StandardInput;
            try
            {
                if (input is not null)
                {
                    await writer.WriteLineAsync(input);
                }
            }
            catch
            {
                // A second failure while flushing must not replace the write failure.
                TryDispose(writer.Close);
                throw;
            }

            writer.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Pipe closure can precede exit notification. Let the helper's result win;
            // WaitForCompletionAsync still enforces timeout, cancellation, and output failures.
            await exitTask;
        }
    }

    private static Task<string> ReadOutputAsync(
        Process process,
        bool standardOutput,
        int maxOutputBytes)
    {
        bool redirected = standardOutput
            ? process.StartInfo.RedirectStandardOutput
            : process.StartInfo.RedirectStandardError;
        if (!redirected)
        {
            return Task.FromResult(string.Empty);
        }

        StreamReader reader = standardOutput ? process.StandardOutput : process.StandardError;
        return ReadOutputAsync(
            reader.BaseStream,
            reader.CurrentEncoding,
            standardOutput ? "standard output" : "standard error",
            maxOutputBytes);
    }

    private static async Task<string> ReadOutputAsync(
        Stream stream,
        Encoding encoding,
        string streamName,
        int maxOutputBytes)
    {
        using MemoryStream output = new(Math.Min(maxOutputBytes, StreamBufferSize));
        int bufferSize = maxOutputBytes < StreamBufferSize
            ? maxOutputBytes + 1
            : StreamBufferSize;
        byte[] buffer = new byte[Math.Max(bufferSize, 1)];

        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    return DecodeOutput(output, encoding);
                }

                if (bytesRead > maxOutputBytes - output.Length)
                {
                    throw new ProcessOutputLimitExceededException(streamName, maxOutputBytes);
                }

                output.Write(buffer, 0, bytesRead);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new ProcessStreamException(streamName, exitCode: null, e);
        }
    }

    private static string DecodeOutput(MemoryStream output, Encoding encoding)
    {
        string value = encoding.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        return value.Length > 0 && value[0] == '\uFEFF' ? value.Substring(1) : value;
    }

    private static async Task<ProcessResult> CompleteAsync(
        Process process,
        Task inputTask,
        Task exitTask,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        await inputTask;
        string output = await outputTask;
        string error = await errorTask;
        await exitTask;
        int exitCode = GetExitCode(process);
        DisposeRedirectedStreams(process);
        return new ProcessResult(exitCode, output, error);
    }

    private async Task<int?> CleanupAfterFailureAsync(
        Process process,
        Task exitTask,
        Task inputTask,
        Task outputTask,
        Task errorTask,
        List<IDisposable> pipeHandles,
        Exception failure)
    {
        // Best-effort termination cannot recover descendants once the direct helper has exited.
        // Do not wait for their inherited pipes; returning the original failure must remain bounded.
        if (!exitTask.IsCompleted && TryTerminate(process))
        {
            await Task.WhenAny(
                exitTask,
                Task.Delay(TerminationWaitTimeout));
        }

        int? exitCode = TryGetExitCode(process);
        if (!exitTask.IsCompleted && exitCode is null)
        {
            failure.Data["CredentialHelperCleanup"] =
                "Credential helper termination could not be confirmed; " +
                "the process may still be running.";
        }

        // Closing a pipe handle does not guarantee pending I/O completes, especially with inherited pipes.
        foreach (IDisposable handle in pipeHandles)
        {
            TryDispose(handle.Dispose);
        }
        _ = IgnoreFailureAsync(inputTask);
        _ = IgnoreFailureAsync(outputTask);
        _ = IgnoreFailureAsync(errorTask);

        return exitCode;
    }

    private static int GetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception e) when (
            e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            throw new ProcessStreamException("exit code", exitCode: null, e);
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception e) when (
            e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static void DisposeRedirectedStreams(Process process)
    {
        if (process.StartInfo.RedirectStandardInput)
        {
            process.StandardInput.Dispose();
        }

        if (process.StartInfo.RedirectStandardOutput)
        {
            process.StandardOutput.Dispose();
        }

        if (process.StartInfo.RedirectStandardError)
        {
            process.StandardError.Dispose();
        }
    }

    private static void CaptureRedirectedPipeHandles(Process process, List<IDisposable> handles)
    {
        // Capture before I/O; acquiring a FileStream's handle can synchronize with a pending operation.
        if (process.StartInfo.RedirectStandardInput)
        {
            handles.Add(GetPipeHandle(process.StandardInput.BaseStream));
        }

        if (process.StartInfo.RedirectStandardOutput)
        {
            handles.Add(GetPipeHandle(process.StandardOutput.BaseStream));
        }

        if (process.StartInfo.RedirectStandardError)
        {
            handles.Add(GetPipeHandle(process.StandardError.BaseStream));
        }
    }

    private static IDisposable GetPipeHandle(Stream stream) =>
        stream is FileStream fileStream ? fileStream.SafeFileHandle : stream;

    private static void TryDispose(Action dispose)
    {
        try
        {
            dispose();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Task IgnoreFailureAsync(Task task) =>
        task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private bool TryTerminate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            if (_terminate is not null)
            {
                return _terminate(process);
            }

#if NETSTANDARD2_0
            process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
            return true;
        }
        catch (Exception e) when (
            e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Cleanup reports unconfirmed exit on the original exception.
            return false;
        }
    }
}
