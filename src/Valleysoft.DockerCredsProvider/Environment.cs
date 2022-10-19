namespace Valleysoft.DockerCredsProvider;

internal interface IEnvironment
{
    string? GetEnvironmentVariable(string variable);
    string GetUserProfilePath();
}

internal class Environment : IEnvironment
{
    public string? GetEnvironmentVariable(string variable) => System.Environment.GetEnvironmentVariable(variable);

    public string GetUserProfilePath() => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
}
