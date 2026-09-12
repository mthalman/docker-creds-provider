# Docker Creds Provider

[![NuGet](https://img.shields.io/nuget/v/Valleysoft.DockerCredsProvider.svg)](https://www.nuget.org/packages/Valleysoft.DockerCredsProvider/)
[![CI](https://github.com/mthalman/docker-creds-provider/actions/workflows/ci.yml/badge.svg)](https://github.com/mthalman/docker-creds-provider/actions/workflows/ci.yml)

Docker Creds Provider is a .NET library for retrieving registry credentials
from Docker-compatible configuration.

## Install the package

```console
dotnet add package Valleysoft.DockerCredsProvider
```

## Retrieve credentials

```csharp
using Valleysoft.DockerCredsProvider;

DockerCredentials credentials =
    await CredsProvider.GetCredentialsAsync("contoso.azurecr.io");

string username = credentials.Username;
string? password = credentials.Password;
string? identityToken = credentials.IdentityToken;
```

Pass a registry hostname or HTTP(S) URL. The library retrieves configured
credentials; it does not authenticate with the registry.

## Configuration

The library supports native credential helpers and inline credentials in
[Docker configuration](https://docs.docker.com/reference/cli/docker/login/#credential-stores)
and [containers/Podman auth files](https://github.com/containers/image/blob/main/docs/containers-auth.json.5.md).

Containers auth files are checked before Docker configuration. Set
`REGISTRY_AUTH_FILE` to use only a specific auth file, or `DOCKER_CONFIG` to
change the Docker configuration directory.

Prefer [credential helpers](https://docs.docker.com/reference/cli/docker/login/#credential-helpers)
over inline credentials: Base64 is not encryption. Never commit or log
passwords or identity tokens.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for the local build and test workflow.

## License

Docker Creds Provider is licensed under the [MIT License](LICENSE).
