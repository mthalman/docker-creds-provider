using System.Diagnostics;
using System.Globalization;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = Encoding.UTF8;

return args switch
{
    ["get"] => await GetCredentialsAsync(),
    ["success"] => await WriteSuccessAsync(),
    ["failure"] => await WriteFailureAsync(),
    ["wait"] => await WaitAsync(),
    ["spawn-child", string childProcessIdPath] => SpawnChild(childProcessIdPath, exitParent: false),
    ["spawn-child-and-exit", string childProcessIdPath] => SpawnChild(childProcessIdPath, exitParent: true),
    ["child-wait"] => await WaitAsync(),
    ["output", string streamName, string byteCount] =>
        await WriteOutputAsync(streamName, int.Parse(byteCount, CultureInfo.InvariantCulture)),
    _ => throw new ArgumentException("Unknown helper fixture command.")
};

static async Task<int> GetCredentialsAsync()
{
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
        Thread.Sleep(Timeout.Infinite);
    }
    return 0;
}

static async Task<int> WriteOutputAsync(string streamName, int byteCount)
{
    Stream stream = streamName switch
    {
        "stdout" => Console.OpenStandardOutput(),
        "stderr" => Console.OpenStandardError(),
        _ => throw new ArgumentException("Unknown output stream.", nameof(streamName))
    };

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
