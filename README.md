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

## Upgrade to 3.0.0

**Breaking change:** When a native credential helper returns malformed JSON,
`CredsProvider.GetCredentialsAsync` now throws `InvalidOperationException`
instead of a top-level `System.Text.Json.JsonException`
([#93](https://github.com/mthalman/docker-creds-provider/pull/93)).
This exception-contract change is the reason for the major version.

Update code that catches `JsonException` for helper responses to catch
`InvalidOperationException` with an exception filter such as
`when (ex.InnerException is System.Text.Json.JsonException)`. The inner exception
is a sanitized `JsonException`: its `Path`, `LineNumber`, and `BytePositionInLine`
properties retain the parser metadata (which can be null), but its message does
not retain the original parser text. Keep existing `JsonException` handling for
configuration-file errors; those errors are not covered by this helper-specific
change.

Helper failure diagnostics no longer include raw standard output or standard
error because they may contain credentials. Use the exception type and available
helper name, failure category, captured-output lengths, exit code, or parser
location instead of parsing helper output from exception messages. Nonzero
helper exits still throw `CredsNotFoundException`; invalid helper response fields
and non-object JSON responses throw `InvalidOperationException`.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for the local build and test workflow.

## License

Docker Creds Provider is licensed under the [MIT License](LICENSE).
