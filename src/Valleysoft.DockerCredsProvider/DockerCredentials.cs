namespace Valleysoft.DockerCredsProvider;

public class DockerCredentials
{
    public DockerCredentials(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }
}
