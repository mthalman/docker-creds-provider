# Docker Creds Provider

[![NuGet](https://img.shields.io/nuget/v/Valleysoft.DockerCredsProvider.svg)](https://www.nuget.org/packages/Valleysoft.DockerCredsProvider/)
[![CI](https://github.com/mthalman/docker-creds-provider/actions/workflows/ci.yml/badge.svg)](https://github.com/mthalman/docker-creds-provider/actions/workflows/ci.yml)

Docker Creds Provider lets .NET applications retrieve registry credentials
from Docker and containers/Podman configuration files. It reads inline
credentials or invokes the configured native credential helper. The library
itself does not log in, contact the registry, or write credentials.

## Install the package

Add the package to your application project:

```console
dotnet add package Valleysoft.DockerCredsProvider
```

## Retrieve credentials

First configure credentials for your registry with Docker, Podman, or a
credential helper. Run your application as a user who can read that
configuration and access its helper. Native helpers must be on the
application process's `PATH`.

In an async method or a C# program with top-level statements:

```csharp
using Valleysoft.DockerCredsProvider;

DockerCredentials credentials =
    await CredsProvider.GetCredentialsAsync("contoso.azurecr.io");

string username = credentials.Username;
string? password = credentials.Password;
string? identityToken = credentials.IdentityToken;
```

Replace `contoso.azurecr.io` with your registry hostname or HTTP(S) URL.
On success, `credentials` contains the configured username and password,
identity token, or both. `Password` and `IdentityToken` are nullable; do not
assume every credential uses a password.

Use `GetCredentialsAsync(registry, cancellationToken)` when the caller needs
to cancel retrieval. Native helpers have a fixed 30-second timeout.

## Configuration

Containers auth files are checked before Docker's `config.json`. Set
`REGISTRY_AUTH_FILE` to use only a specific containers-format auth file, or
`DOCKER_CONFIG` to change the Docker configuration directory. A selected
helper or invalid credential does not fall back to another file.

Prefer [credential helpers](https://docs.docker.com/reference/cli/docker/login/#credential-helpers)
over inline credentials: Base64 is not encryption. Never commit or log
passwords or identity tokens.

See the [configuration and credential lookup reference](https://github.com/mthalman/docker-creds-provider/blob/main/docs/configuration.md)
for file order, registry matching, and helper setup.

## Contribute

Report bugs or propose changes in
[GitHub issues](https://github.com/mthalman/docker-creds-provider/issues).
See the [contributor guide](https://github.com/mthalman/docker-creds-provider/blob/main/CONTRIBUTING.md)
for local setup, tests, and pull request requirements. Maintainers use the
[release procedure](https://github.com/mthalman/docker-creds-provider/blob/main/MAINTAINERS.md#publish-a-release).

## License

Docker Creds Provider is licensed under the
[MIT License](https://github.com/mthalman/docker-creds-provider/blob/main/LICENSE).
