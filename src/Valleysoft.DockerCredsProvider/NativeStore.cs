using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Valleysoft.DockerCredsProvider;
 
internal class NativeStore : ICredStore
{
    private readonly string credHelperName;
    private readonly IProcessService processService;

    // A username of <token> indicates the secret is an identity token
    // See https://docs.docker.com/engine/reference/commandline/login/#credential-helper-protocol
    private const string TokenSpecifier = "<token>";

    public NativeStore(string credHelperName, IProcessService processService)
    {
        this.credHelperName = credHelperName;
        this.processService = processService;
    }

    public async Task<DockerCredentials> GetCredentialsAsync(string registry)
    {
        const string Username = "Username";
        const string Secret = "Secret";
        string output = ExecuteCredHelper("get", registry);

        using JsonDocument configDoc = await JsonDocument.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(output)));

        string? username = null;
        if (configDoc.RootElement.TryGetProperty(Username, out JsonElement usernameElement))
        {
            username = usernameElement.GetString();
        }

        string? password = null;
        if (configDoc.RootElement.TryGetProperty(Secret, out JsonElement secretElement))
        {
            password = secretElement.GetString();
        }

        if (username is null)
        {
            throw new InvalidOperationException($"Output of cred helper doesn't contain '{Username}': {output}");
        }

        if (password is null)
        {
            throw new InvalidOperationException($"Output of cred helper doesn't contain '{Secret}': {output}");
        }

        string? identityToken = null;
        if (username == TokenSpecifier)
        {
            identityToken = password;
            password = null;
        }

        return new DockerCredentials(username, password, identityToken);
    }

    private string ExecuteCredHelper(string command, string? input)
    {
        ProcessStartInfo startInfo = new($"docker-credential-{credHelperName}", command)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        StringBuilder stdOutput = new();
        StringBuilder stdError = new();

        int exitCode;
        try
        {
            exitCode = processService.Run(startInfo, input, GetDataReceivedHandler(stdOutput), GetDataReceivedHandler(stdError));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2)
        {
            throw new InvalidOperationException($"Unable to execute the '{startInfo.FileName}' executable. Be sure that Docker is installed and that its bin location is specified in your environment's path.", e);
        }

        if (exitCode != 0)
        {
            string err = stdError.Length > 0 ? stdError.ToString() : stdOutput.ToString();

            throw new CredsNotFoundException(
                $"Failed to execute '{startInfo.FileName} {startInfo.Arguments}':" +
                Environment.NewLine + err);
        }

        return stdOutput.ToString();
    }

    private static Action<string?> GetDataReceivedHandler(StringBuilder stringBuilder)
    {
        return new Action<string?>(value =>
        {
            if (value is not null)
            {
                stringBuilder.AppendLine(value);
            }
        });
    }
}
