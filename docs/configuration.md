# Configuration and credential lookup

The library retrieves credentials already configured on the machine. It does
not perform registry login or update auth files. A native helper runs as an
external process; its own behavior depends on the helper you configure.
The library's configuration
locations and helper access belong to the application process, not necessarily
the user or shell that last ran Docker.

## Configuration files

If `REGISTRY_AUTH_FILE` is nonempty, the library checks only that file and
treats it as [containers-format configuration](https://github.com/containers/image/blob/main/docs/containers-auth.json.5.md). A missing file or unmatched
registry does not enable the default search.

Otherwise, files are checked in this order:

| Priority | Location | Format |
| --- | --- | --- |
| 1 | `containers/auth.json` under `XDG_RUNTIME_DIR`, when nonempty | Containers |
| 2 | `containers/auth.json` under `XDG_CONFIG_HOME` | Containers |
| 3 | `config.json` under `DOCKER_CONFIG` | Docker |

Paths use the operating system's directory separators. For priority 2, an
unset or empty `XDG_CONFIG_HOME` uses `XDG_CONFIG_DIR`, the legacy alias.
If both are unset or empty, the directory is `.config` under the process
user's profile. `XDG_CONFIG_DIR` is not an additional search location when
`XDG_CONFIG_HOME` is set, even if its auth file is missing.

An unset or empty `DOCKER_CONFIG` uses `.docker` under the process user's
profile. `DOCKER_CONFIG` names a directory; `REGISTRY_AUTH_FILE` names a file.

Missing files are skipped. An existing file without a matching entry allows
lookup to continue to the next file. Invalid JSON, an invalid selected entry,
or a selected helper failure stops retrieval. Later files do not repair an
earlier configuration error.

See the [discovery implementation](../src/Valleysoft.DockerCredsProvider/CredsProvider.cs)
and [discovery tests](../src/Valleysoft.DockerCredsProvider.Test/ConfigDiscoveryTests.cs)
for the file-selection rules.

## Credential selection within a file

The library chooses the first applicable source within each file:

1. A registry-specific entry in `credHelpers`.
2. The global `credsStore`, in Docker-format files only.
3. A matching inline entry in `auths`.

Containers-format files ignore `credsStore`. Once a helper or inline entry is
selected, retrieval does not fall back to another entry or file if it fails.

### Native credential helpers

Use the credential helper already configured for Docker, such as the one
provided by Docker Desktop. You do not need to write a custom helper or
change its configuration to use this library.

The library reads `credHelpers` and `credsStore` from the selected config
file and invokes the configured helper. Its executable must be available
on the application process's `PATH`; on Windows, discovery also uses
`PATHEXT`. If Docker can retrieve credentials but your application cannot,
check whether the two processes use the same configuration and helper path.

A registry-specific `credHelpers` entry takes priority over `credsStore`.
The global store takes priority over inline `auths`, even if that helper
has no credential for the requested registry.

The returned `DockerCredentials` contains either the helper's password or
identity token. For an identity-token response, `Password` is null.

Each helper invocation has a fixed 30-second timeout and a 1 MiB byte limit
for each of standard output and standard error. Neither setting has a public
configuration option. Use the cancellation-token overload to cancel earlier.
Helper failure diagnostics omit captured output because it may contain secrets.

See Docker's [credential store documentation](https://docs.docker.com/reference/cli/docker/login/#credential-stores)
for setup. Helper authors can refer to the
[credential helper protocol](https://docs.docker.com/reference/cli/docker/login/#credential-helper-protocol).

### Inline credentials

Inline `auth` values encode UTF-8 `username:password` with Base64. The first
colon separates the username from the password; additional colons remain
part of the password.

This illustrative entry encodes `user:example-password`. These are dummy
values, not credentials that can authenticate with a registry:

```json
{
  "auths": {
    "registry.example.com": {
      "auth": "dXNlcjpleGFtcGxlLXBhc3N3b3Jk"
    }
  }
}
```

With this entry selected, a lookup for `registry.example.com` returns
`Username == "user"`, `Password == "example-password"`, and a null
`IdentityToken`.

An inline entry can also contain `identitytoken`. It still requires `auth`
with a nonempty decoded username. A non-null identity token allows the
decoded `auth` to omit a password. When both a password and identity token
are configured, both are returned.

Base64 is not encryption. Prefer a native helper for stored credentials.
Do not commit auth files or print returned passwords or tokens.
See the [inline implementation](../src/Valleysoft.DockerCredsProvider/EncodedStore.cs).

## Registry names and matching

Pass a hostname, an authority with a port, or an HTTP(S) URL. A repository
path can select namespace-specific containers credentials. Image tags and
digests are not accepted as part of the path.

Examples of accepted inputs include `registry.example.com`,
`registry.example.com:5000/team/image`, and
`https://registry.example.com/team/image`. User information, queries,
fragments, surrounding whitespace, and non-HTTP(S) schemes are rejected.

### Docker configuration

Docker inline credentials are registry-scoped. Paths in a request do not
select repository-specific credentials. Matching prefers the canonical key,
then HTTPS and HTTP authority keys, then normalized legacy entries.
Inline host matching permits case differences.

For lowercase `docker.io`, `index.docker.io`, and `registry-1.docker.io`,
the canonical Docker key is `https://index.docker.io/v1/`. Legacy inline
Docker Hub keys can also match.

`credHelpers` keys require exact, case-sensitive matches to the canonical
Docker key. Use the historical URL above for a Docker Hub helper, not
`docker.io`. A global `credsStore` receives the canonical Docker key.

### Containers configuration

Containers inline matching is case-sensitive. It tries the exact key first,
then the most specific namespace, its parents, and finally the registry host.
For `registry.example.com/team/image`, those namespace candidates are
`registry.example.com/team/image`, `registry.example.com/team`, and
`registry.example.com`. A credential for `team` does not match `team-other`.

After those candidates, normalized registry entries can match legacy URL
keys and lowercase Docker Hub aliases. Alias matching does not extend to
namespace entries for a different hostname.

Containers `credHelpers` keys match only the exact registry authority,
including any port. They do not match namespaces or normalize Docker Hub
aliases. For `registry.example.com:5000/team/image`, use
`registry.example.com:5000` as the helper key.
