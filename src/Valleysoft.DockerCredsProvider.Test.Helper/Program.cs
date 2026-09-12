using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = Encoding.UTF8;

return args switch
{
    ["get"] => await GetCredentialsAsync(),
    ["success"] => await WriteSuccessAsync(),
    ["failure"] => await WriteFailureAsync(),
    ["closed-input", string releasePath, string outcome] =>
        await CloseInputBeforeExitAsync(releasePath, outcome),
    ["wait"] => await WaitAsync(),
    ["spawn-child", string childProcessIdPath] => SpawnChild(childProcessIdPath, exitParent: false),
    ["spawn-child-and-exit", string childProcessIdPath] => SpawnChild(childProcessIdPath, exitParent: true),
    ["child-wait"] => await WaitAsync(),
    ["output", string streamName, string byteCount] =>
        await WriteOutputAsync(streamName, int.Parse(byteCount, CultureInfo.InvariantCulture)),
    ["pipe-pressure", string byteCount] =>
        await ExercisePipesAsync(int.Parse(byteCount, CultureInfo.InvariantCulture)),
    ["overflow-blocked-input", string streamName, string byteCount] =>
        await ExercisePipesAsync(int.Parse(byteCount, CultureInfo.InvariantCulture), streamName),
    ["utf8-output", string streamName, string output] =>
        await WriteUtf8OutputAsync(streamName, output),
    _ => throw new ArgumentException("Unknown helper fixture command.")
};

static async Task<int> GetCredentialsAsync()
{
    if (Environment.GetEnvironmentVariable("DOCKER_CREDS_TEST_SCENARIO") is string scenario)
    {
        return await RunProtocolScenarioAsync(scenario);
    }

    string? registry = await Console.In.ReadLineAsync();

    switch (registry)
    {
        case "password":
            await Console.Out.WriteAsync(
                "{\"Username\":\"fixture-user\",\"Secret\":\"fixture-password\"}");
            return 0;
        case "token":
            await Console.Out.WriteAsync(
                "{\"Username\":\"<token>\",\"Secret\":\"fixture-token\"}");
            return 0;
        case "unicode":
            await Console.Out.WriteAsync(
                "{\"Username\":\"fixture-üser\",\"Secret\":\"pässword-🔒\"}");
            return 0;
        case "unicode-registry-例":
            await Console.Out.WriteAsync(
                "{\"Username\":\"fixture-user\",\"Secret\":\"fixture-password\"}");
            return 0;
        case "empty-secret":
            await Console.Out.WriteAsync(
                "{\"Username\":\"fixture-user\",\"Secret\":\"\"}");
            return 0;
        case "extra-fields":
            await Console.Out.WriteAsync(
                "{\"Username\":\"fixture-user\",\"Secret\":\"fixture-password\",\"ServerURL\":\"extra-fields\"}");
            return 0;
        case "missing-username":
            await Console.Out.WriteAsync("{\"Secret\":\"fixture-secret\"}");
            return 0;
        case "missing-secret":
            await Console.Out.WriteAsync("{\"Username\":\"fixture-user\"}");
            return 0;
        case "null-username":
            await Console.Out.WriteAsync("{\"Username\":null,\"Secret\":\"fixture-secret\"}");
            return 0;
        case "null-secret":
            await Console.Out.WriteAsync("{\"Username\":\"fixture-user\",\"Secret\":null}");
            return 0;
        case "numeric-username":
            await Console.Out.WriteAsync("{\"Username\":42,\"Secret\":\"fixture-secret\"}");
            return 0;
        case "object-secret":
            await Console.Out.WriteAsync(
                "{\"Username\":\"fixture-user\",\"Secret\":{\"value\":\"fixture-secret\"}}");
            return 0;
        case "malformed":
            await Console.Out.WriteAsync(
                "{\"Username\":\"fixture-user\",\"Secret\":\"fixture-secret\"");
            return 0;
        case "non-object":
            await Console.Out.WriteAsync("[\"fixture-secret\"]");
            return 0;
        case "failure":
            return await WriteFailureAsync();
        default:
            await Console.Error.WriteAsync("unknown fixture registry");
            return 2;
    }
}

static async Task<int> RunProtocolScenarioAsync(string scenario)
{
    using MemoryStream input = new();
    await Console.OpenStandardInput().CopyToAsync(input);

    if (Environment.GetEnvironmentVariable("DOCKER_CREDS_TEST_READY") is string readyPath)
    {
        File.WriteAllText(readyPath, "ready");
        string releasePath = Environment.GetEnvironmentVariable("DOCKER_CREDS_TEST_RELEASE")
            ?? throw new InvalidOperationException("Missing fixture release path.");
        while (!File.Exists(releasePath))
        {
            await Task.Delay(10);
        }
    }

    if (scenario is "overflow-stdout" or "overflow-stderr")
    {
        string streamName = scenario == "overflow-stdout" ? "stdout" : "stderr";
        await WriteUtf8OutputAsync(streamName, "fixture-overflow-secret");
        return await WriteOutputAsync(streamName, 1024 * 1024);
    }

    if (scenario is not ("protocol" or "chunked"))
    {
        throw new ArgumentException("Unknown protocol scenario.", nameof(scenario));
    }

    string username = Convert.ToBase64String(input.ToArray());
    byte[] response = Encoding.UTF8.GetBytes(
        $"{{\"Username\":\"{username}\",\"Secret\":\"fixture-\u00e4-\U0001F512:{username}\"}}");
    Stream output = Console.OpenStandardOutput();
    if (scenario == "chunked")
    {
        for (int index = 0; index < response.Length; index++)
        {
            await output.WriteAsync(response.AsMemory(index, 1));
            await output.FlushAsync();
            // Separate writes also split UTF-8 code points across pipe reads.
            await Task.Delay(1);
        }
    }
    else
    {
        await output.WriteAsync(response);
        await output.FlushAsync();
    }
    return 0;
}

static async Task<int> WriteSuccessAsync()
{
    string? registry = await Console.In.ReadLineAsync();
    await Console.Out.WriteAsync(
        $"{{\"Username\":\"fixture-user\",\"Secret\":\"fixture-secret\",\"ServerURL\":\"{registry}\"}}");
    return 0;
}

static async Task<int> WriteFailureAsync()
{
    await Console.Out.WriteAsync("fixture-stdout-secret");
    await Console.Error.WriteAsync("fixture-stderr-secret");
    return 17;
}

static async Task<int> WaitAsync()
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

static async Task<int> CloseInputBeforeExitAsync(string releasePath, string outcome)
{
    // Console.OpenStandardInput() does not own the OS handle; disposing it leaves the pipe open.
    bool closed = OperatingSystem.IsWindows()
        ? NativeMethods.CloseHandle(NativeMethods.GetStdHandle(-10))
        : NativeMethods.Close(0) == 0;
    if (!closed)
    {
        throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    Console.Out.WriteLine("ready");
    Console.Out.Flush();
    while (!File.Exists(releasePath))
    {
        await Task.Delay(10);
    }

    if (outcome is "stdout" or "stderr")
    {
        await WriteOutputAsync(outcome, 1024 * 1024 + 1);
        return await WaitAsync();
    }

    int exitCode = await WriteFailureAsync();
    return outcome switch
    {
        "success" => 0,
        "failure" => exitCode,
        _ => throw new ArgumentException("Unknown closed-input outcome.", nameof(outcome))
    };
}

static int SpawnChild(string childProcessIdPath, bool exitParent)
{
    using Process childProcess = Process.Start(new ProcessStartInfo(
        Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to locate the helper executable."),
        "child-wait")
    {
        CreateNoWindow = true,
        UseShellExecute = false
    }) ?? throw new InvalidOperationException("Unable to start the child helper.");

    File.WriteAllText(
        childProcessIdPath,
        childProcess.Id.ToString(CultureInfo.InvariantCulture));
    if (!exitParent)
    {
        Console.Out.WriteLine("ready");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }
    return 0;
}

static async Task<int> WriteOutputAsync(string streamName, int byteCount)
{
    Stream stream = OpenOutputStream(streamName);
    byte[] buffer = new byte[81920];
    Array.Fill(buffer, (byte)'x');

    while (byteCount > 0)
    {
        int count = Math.Min(buffer.Length, byteCount);
        await stream.WriteAsync(buffer.AsMemory(0, count));
        byteCount -= count;
    }

    await stream.FlushAsync();
    return 0;
}

static async Task<int> ExercisePipesAsync(int byteCount, string? overflowStream = null)
{
    await Task.WhenAll(
        WriteOutputAsync("stdout", byteCount + (overflowStream == "stdout" ? 1 : 0)),
        WriteOutputAsync("stderr", byteCount + (overflowStream == "stderr" ? 1 : 0)));

    if (overflowStream is not null)
    {
        return await WaitAsync();
    }

    string? input = await Console.In.ReadLineAsync();
    if (input != new string('x', byteCount))
    {
        throw new InvalidOperationException("Helper did not receive the expected input.");
    }

    return 0;
}

static async Task<int> WriteUtf8OutputAsync(string streamName, string output)
{
    Stream stream = OpenOutputStream(streamName);
    await stream.WriteAsync(Encoding.UTF8.GetBytes(output));
    await stream.FlushAsync();
    return 0;
}

static Stream OpenOutputStream(string streamName) => streamName switch
{
    "stdout" => Console.OpenStandardOutput(),
    "stderr" => Console.OpenStandardError(),
    _ => throw new ArgumentException("Unknown output stream.", nameof(streamName))
};

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static extern int Close(int fileDescriptor);
}
