using System.Threading.Tasks;

namespace DockerCredsProvider
{
    internal interface ICredStore
    {
        Task<DockerCredentials> GetCredentialsAsync(string registry);
    }
}
