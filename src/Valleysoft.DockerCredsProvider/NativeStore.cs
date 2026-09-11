using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static Valleysoft.DockerCredsProvider.ListHelper;

namespace Valleysoft.DockerCredsProvider;
 
internal class NativeStore : ICredStore
{
    internal static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);
    internal const int HelperOutputLimit = 1024 * 1024;
    private static readonly Encoding HelperEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);

    private readonly string _credHelperName;
    private readonly IProcessService _processService;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironment _environment;

    // A username of <token> indicates the secret is an identity token
    // See https://docs.docker.com/engine/reference/commandline/login/#credential-helper-protocol
    private const string TokenSpecifier = "<token>";

    public NativeStore(string credHelperName, IProcessService processService, IFileSystem fileSystem, IEnvironment environment)
    {
        _credHelperName = credHelperName;
        _processService = processService;
        _fileSystem = fileSystem;
        _environment = environment;
    }

    public async Task<DockerCredentials> GetCredentialsAsync(string registry, CancellationToken cancellationToken)
    {
        const string Username = "Username";
        const string Secret = "Secret";
        string output = await ExecuteCredHelperAsync("get", registry, cancellationToken);
        byte[] outputBytes = Encoding.UTF8.GetBytes(output);
        using MemoryStream outputStream = new(outputBytes);

        JsonDocument configDoc;
        try
        {
            configDoc = await JsonDocument.ParseAsync(
                outputStream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException e)
        {
            JsonException sanitizedException = new(
                "Credential helper response could not be parsed as JSON.",
                e.Path,
                e.LineNumber,
                e.BytePositionInLine);

            throw new InvalidOperationException(
                $"Credential helper '{GetHelperName()}' returned malformed JSON " +
                $"({output.Length} captured characters; line {e.LineNumber?.ToString() ?? "unknown"}, " +
                $"byte position {e.BytePositionInLine?.ToString() ?? "unknown"}).",
                sanitizedException);
        }

        using (configDoc)
        {
            if (configDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Credential helper '{GetHelperName()}' returned an invalid response whose JSON root " +
                    $"was {configDoc.RootElement.ValueKind} instead of an object " +
                    $"({output.Length} captured characters).");
            }

            string username = GetRequiredString(configDoc.RootElement, Username, output.Length);
            string? password = GetRequiredString(configDoc.RootElement, Secret, output.Length);

            string? identityToken = null;
            if (username == TokenSpecifier)
            {
                identityToken = password;
                password = null;
            }

            return new DockerCredentials(username, password, identityToken);
        }
    }

    private string GetRequiredString(JsonElement rootElement, string propertyName, int outputLength)
    {
        if (rootElement.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            element.GetString() is string value)
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Credential helper '{GetHelperName()}' returned an invalid response without a " +
            $"non-null string '{propertyName}' field ({outputLength} captured characters).");
    }

    private string GetHelperName() => $"docker-credential-{_credHelperName}";

    private string? CheckForCandidateOnPath(List<string> candidates, string path) =>
        candidates
            .Select(candidate => Path.Combine(path, candidate))
            .FirstOrDefault(absoluteCandidatePath => _fileSystem.FileExists(absoluteCandidatePath));

    private string? ProbePathForNames(List<string> commandNameCandidates)
    {
        if (_environment.GetEnvironmentVariable("PATH") is string path  && path is not null) {
            return path
                .Split(Path.PathSeparator)
                .Select(pathDir => CheckForCandidateOnPath(commandNameCandidates, pathDir))
                .FirstOrDefault(candidate => candidate is not null);
        } else {
            return null;
        }
    }

    private List<string> ExtendViaPathExt(string commandName)
    {
        if (_environment.GetEnvironmentVariable("PATHEXT") is string pathext && pathext is not null) {
            var executableExtensions = pathext.Split(';');
            // order is important here - the raw name should come first
            var variations = new List<string>(1 + executableExtensions.Length){
                commandName
            };
            // but PATHEXT determines the probing behavior if the raw form isn't found
            variations.AddRange(executableExtensions.Select(ext => Path.ChangeExtension(commandName, ext)));
            return variations;
        } else {
            return Singleton(commandName);
        }
    }

    private List<string> CommandNameCandidates(string toolName) {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return ExtendViaPathExt(toolName);
        } else {
            return Singleton(toolName);
        }
    }

    public string? LocateExecutable(string executableName) => ProbePathForNames(CommandNameCandidates(executableName));

    private async Task<string> ExecuteCredHelperAsync(string command, string? input, CancellationToken cancellationToken)
    {   
        var helperName = GetHelperName();
        var commandPath = LocateExecutable(helperName) ?? throw new InvalidOperationException($"Unable to locate {helperName} on the system PATH. Be sure that the directory containing {helperName} is on your PATH.");
        ProcessStartInfo startInfo = new(commandPath, command)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = HelperEncoding,
            StandardErrorEncoding = HelperEncoding
        };

        ProcessResult result;
        try
        {
            result = await _processService.RunAsync(
                startInfo,
                input,
                HelperOutputLimit,
                HelperTimeout,
                cancellationToken);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2)
        {
            throw new InvalidOperationException(
                $"Unable to execute credential helper '{helperName}'. Be sure that Docker is installed " +
                "and that its bin location is specified in your environment's path.",
                e);
        }
        catch (ProcessOutputLimitExceededException e)
        {
            throw new InvalidOperationException(
                $"Credential helper '{helperName}' exceeded the {e.Limit}-byte {e.StreamName} limit. " +
                "Helper output was omitted because it may contain credentials.",
                e);
        }
        catch (ProcessStreamException e)
        {
            string exitCode = e.ExitCode?.ToString() ?? "unavailable";
            throw new InvalidOperationException(
                $"Credential helper '{helperName}' encountered a {e.StreamName} failure " +
                $"(exit code {exitCode}). Helper output was omitted because it may contain credentials.",
                e);
        }

        if (result.ExitCode != 0)
        {
            throw new CredsNotFoundException(
                $"Credential helper '{helperName}' exited with code {result.ExitCode} " +
                $"(captured standard output length: {result.StandardOutput.Length} characters; " +
                $"captured standard error length: {result.StandardError.Length} characters). " +
                "Helper output was omitted because it may contain credentials.");
        }

        return result.StandardOutput;
    }
}
