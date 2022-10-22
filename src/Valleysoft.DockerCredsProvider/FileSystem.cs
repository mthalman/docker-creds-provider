using System.Runtime.InteropServices;

namespace Valleysoft.DockerCredsProvider;

internal interface IFileSystem
{
    Stream FileOpenRead(string path);
    bool FileExists(string path);

    string? LocateExecutable(string executableName);
}

internal class FileSystem : IFileSystem
{
    public Stream FileOpenRead(string path) => File.OpenRead(path);

    public bool FileExists(string path) => File.Exists(path);

    private string? CheckForCandidateOnPath(List<string> candidates, string path) =>
        candidates
            .Select(candidate => Path.Combine(path, candidate))
            .FirstOrDefault(absoluteCandidatePath => this.FileExists(absoluteCandidatePath));

    private string? ProbePathForNames(List<string> commandNameCandidates) =>
        Environment.GetEnvironmentVariable("PATH")
            .Split(Path.PathSeparator)
            .Select(pathDir => this.CheckForCandidateOnPath(commandNameCandidates, pathDir))
            .FirstOrDefault(candidate => candidate is not null);

    private static List<T> Singleton<T>(T item) => new List<T>(1) { item };

    private List<string> ExtendViaPathExt(string commandName) {
        if (Environment.GetEnvironmentVariable("PATHEXT") is string pathext) {
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

    public string? LocateExecutable(string executableName) => this.ProbePathForNames(this.CommandNameCandidates(executableName));
}
