using System.Diagnostics;

namespace Valleysoft.DockerCredsProvider;

internal interface IProcessService
{
    Task<int> RunAsync(
        ProcessStartInfo startInfo,
        string? input,
        Action<string?> outputDataReceived,
        Action<string?> errorDataReceived,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal class ProcessService : IProcessService
{
    private readonly Action<Process>? _processStarted;

    public ProcessService()
    {
    }

    internal ProcessService(Action<Process> processStarted)
    {
        _processStarted = processStarted;
    }

    public async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        string? input,
        Action<string?> outputDataReceived,
        Action<string?> errorDataReceived,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        TaskCompletionSource<int> exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> outputCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> errorCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        process.Exited += (_, _) => exitCompletion.TrySetResult(process.ExitCode);
        process.OutputDataReceived += (_, args) =>
        {
            outputDataReceived(args.Data);
            if (args.Data is null)
            {
                outputCompletion.TrySetResult(true);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            errorDataReceived(args.Data);
            if (args.Data is null)
            {
                errorCompletion.TrySetResult(true);
            }
        };

        bool started = false;
        try
        {
            process.Start();
            started = true;
            _processStarted?.Invoke(process);

            Task timeoutTask = Task.Delay(timeout);
            Task cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);

            if (process.StartInfo.RedirectStandardError)
            {
                process.BeginErrorReadLine();
            }
            else
            {
                errorCompletion.TrySetResult(true);
            }

            if (process.StartInfo.RedirectStandardOutput)
            {
                process.BeginOutputReadLine();
            }
            else
            {
                outputCompletion.TrySetResult(true);
            }

            Task inputTask = WriteInputAsync(process, input);
            _ = inputTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            Task completedTask = await Task.WhenAny(exitCompletion.Task, inputTask, timeoutTask, cancellationTask);

            if (completedTask == inputTask)
            {
                await inputTask;
                completedTask = await Task.WhenAny(exitCompletion.Task, timeoutTask, cancellationTask);
            }

            if (completedTask == exitCompletion.Task || exitCompletion.Task.IsCompleted)
            {
                return await CompleteAsync(inputTask, exitCompletion.Task, outputCompletion.Task, errorCompletion.Task);
            }

            if (completedTask == cancellationTask || cancellationToken.IsCancellationRequested)
            {
                Terminate(process);
                await exitCompletion.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!Terminate(process))
            {
                return await CompleteAsync(inputTask, exitCompletion.Task, outputCompletion.Task, errorCompletion.Task);
            }

            await exitCompletion.Task;
            throw new TimeoutException(
                $"Process '{startInfo.FileName} {startInfo.Arguments}' exceeded the timeout of {timeout}.");
        }
        catch
        {
            if (started && !exitCompletion.Task.IsCompleted)
            {
                Terminate(process);
                await exitCompletion.Task;
            }

            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, string? input)
    {
        if (input is null)
        {
            return;
        }

        try
        {
            await process.StandardInput.WriteLineAsync(input);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task<int> CompleteAsync(Task inputTask, Task<int> exitTask, Task outputTask, Task errorTask)
    {
        int exitCode = await exitTask;
        await Task.WhenAll(inputTask, outputTask, errorTask);
        return exitCode;
    }

    private static bool Terminate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

#if NETSTANDARD2_0
            process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
