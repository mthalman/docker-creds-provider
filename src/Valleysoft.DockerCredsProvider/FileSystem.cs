using System.Runtime.InteropServices;

namespace Valleysoft.DockerCredsProvider;

internal interface IFileSystem
{
    Stream FileOpenRead(string path);
    bool FileExists(string path);
}

internal class FileSystem : IFileSystem
{
    private readonly IEnvironment environment;

    public FileSystem(IEnvironment environment) {
        this.environment = environment;
    }

    public Stream FileOpenRead(string path) => File.OpenRead(path);

    public bool FileExists(string path) => File.Exists(path);
}
