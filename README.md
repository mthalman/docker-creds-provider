# Docker Creds Provider

[![NuGet](https://img.shields.io/nuget/v/Valleysoft.DockerCredsProvider.svg)](https://www.nuget.org/packages/Valleysoft.DockerCredsProvider/)
[![CI](https://github.com/mthalman/docker-creds-provider/actions/workflows/ci.yml/badge.svg)](https://github.com/mthalman/docker-creds-provider/actions/workflows/ci.yml)

Docker Creds Provider is a .NET library for retrieving registry credentials
from Docker-compatible configuration.

## Install the package

Install
[Valleysoft.DockerCredsProvider](https://www.nuget.org/packages/Valleysoft.DockerCredsProvider/)
from NuGet:

```console
dotnet add package Valleysoft.DockerCredsProvider
```

## Retrieve credentials

Pass a registry name or URL to `CredsProvider.GetCredentialsAsync`:

```csharp
using Valleysoft.DockerCredsProvider;

DockerCredentials credentials =
    await CredsProvider.GetCredentialsAsync("contoso.azurecr.io");
```

Pass a cancellation token to stop credential retrieval when the calling
operation is canceled:

```csharp
DockerCredentials credentials =
    await CredsProvider.GetCredentialsAsync("contoso.azurecr.io", cancellationToken);
```

The returned `DockerCredentials` object provides the username and either a
password or identity token. The library follows
[Docker's credential configuration](https://docs.docker.com/reference/cli/docker/login/#credential-stores)
to locate the credentials.

If `REGISTRY_AUTH_FILE` is set, the library checks only that file. Otherwise,
it checks these locations in order:

1. `$XDG_RUNTIME_DIR/containers/auth.json`, when `XDG_RUNTIME_DIR` is set.
2. The persistent containers auth file: `$XDG_CONFIG_HOME/containers/auth.json`
   when `XDG_CONFIG_HOME` is set, `$XDG_CONFIG_DIR/containers/auth.json` when
   it is not, or `$HOME/.config/containers/auth.json` when neither variable is
   set. `XDG_CONFIG_DIR` is deprecated and supported only as a compatibility
   alias.
3. `$DOCKER_CONFIG/config.json`, or `$HOME/.docker/config.json` when
   `DOCKER_CONFIG` is unset.

Native credential helpers have a 30-second timeout; an expired timeout throws
`TimeoutException`, while caller cancellation throws
`OperationCanceledException`.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for the local build and test workflow.
See [MAINTAINERS.md](MAINTAINERS.md) for release labeling and publishing.

## License

Docker Creds Provider is licensed under the [MIT License](LICENSE).
