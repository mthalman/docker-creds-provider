using System;
using System.Text;
using System.Threading.Tasks;

namespace Valleysoft.DockerCredsProvider
{
    internal class EncodedStore : ICredStore
    {
        private readonly string credentialEncoding;

        public EncodedStore(string credentialEncoding)
        {
            this.credentialEncoding = credentialEncoding;
        }

        public Task<DockerCredentials> GetCredentialsAsync(string registry)
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(credentialEncoding));
            string[] parts = decoded.Split(':');
            return Task.FromResult(new DockerCredentials(parts[0], parts[1]));
        }
    }
}
