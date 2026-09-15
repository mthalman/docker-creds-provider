### Registry credential matching follows the configuration format

Review your configuration if you use credential helpers, containers/Podman auth
files, or multiple inline credentials for the same registry. You generally do
not need to change calls that pass a valid hostname and use a single matching
Docker inline credential.

Docker Desktop users with its automatically configured credential store generally
do not need to edit their configuration for the Docker Hub key change below.
The helper example applies to a custom `credHelpers` entry.

Docker rules apply to Docker's configuration location, including the directory
selected by `DOCKER_CONFIG`. Containers rules apply to containers auth files and
any file selected through `REGISTRY_AUTH_FILE`, regardless of its filename.

#### Previous behavior

All files used the same rules: inline credentials matched the first
case-insensitive host match, ignoring namespace paths. `credHelpers` keys
matched the original API argument exactly, and helpers received that argument
unchanged. A global `credsStore` worked in both Docker and containers auth files.
Malformed non-null arguments were not explicitly rejected before lookup.

##### Example: Docker Hub credential helper

Suppose you configured this custom entry in Docker's `config.json`. The value
`example` stands for an installed helper named `docker-credential-example`:

```json
{
  "credHelpers": {
    "index.docker.io": "example"
  }
}
```

This call selected `docker-credential-example` and sent `index.docker.io` to
the helper on standard input:

```csharp
await CredsProvider.GetCredentialsAsync("index.docker.io");
```

##### Example: containers namespace credentials

If a containers auth file had only an `auths` entry for
`registry.example.com/team`, both of these calls used that entry:

```csharp
await CredsProvider.GetCredentialsAsync("registry.example.com/team/image");
await CredsProvider.GetCredentialsAsync("registry.example.com/other/image");
```

The second call received the team's credentials even though it requested an
unrelated repository.

#### New behavior

Docker lookup is registry-scoped. Containers lookup respects case-sensitive
namespace keys, checking the requested namespace, its parents, then the registry.
Unrelated bare namespace entries no longer supply registry-wide credentials.
Containers files ignore `credsStore`.

Helper keys are registry-scoped and case-sensitive. Helpers receive the selected
registry key, not the original URL or repository path. For Docker, lowercase
Docker Hub aliases use `https://index.docker.io/v1/`.

Inline credential selection prefers canonical or exact matches over other
equivalent keys, rather than taking the first host match. Duplicate keys use
their last definition. Invalid arguments now throw `ArgumentException` before
lookup; null still throws `ArgumentNullException`.

##### Example: Docker Hub credential helper

With the unchanged configuration, this call no longer selects the
`index.docker.io` helper entry:

```csharp
await CredsProvider.GetCredentialsAsync("index.docker.io");
```

The API argument is still valid, but Docker helper lookup now uses
`https://index.docker.io/v1/`. If no other credentials match, lookup throws
`CredsNotFoundException`. The required configuration edit is shown under
Recommended action.

##### Example: containers namespace credentials

With the same `auths` entry for `registry.example.com/team`, the first call still
uses the team's credentials:

```csharp
await CredsProvider.GetCredentialsAsync("registry.example.com/team/image");
```

The unrelated repository no longer matches that entry:

```csharp
await CredsProvider.GetCredentialsAsync("registry.example.com/other/image");
```

If no other credentials match, the second call throws `CredsNotFoundException`.

#### Type of breaking change

**Behavioral change.** Existing calls can select different credentials or helpers,
stop finding credentials, send a different server key to a helper, or throw
`ArgumentException` instead of reaching configuration lookup. Public method
signatures are unchanged.

#### Reason for change

Docker and containers auth files have different registry and namespace
semantics. Format-aware lookup prevents unrelated namespace credentials from
being selected and makes helper lookup consistent with the server key supplied
to the helper.

#### Recommended action

Apply only the changes that match your configuration. A configuration key is
not the same as the argument you pass to `GetCredentialsAsync`: changing a
`credHelpers` key does not require changing a valid API argument.

##### Docker Hub: change only Docker credential-helper keys

You can keep calling `GetCredentialsAsync("index.docker.io")`. Whether you need
to edit a configuration file depends on where `index.docker.io` appears:

| Where you use `index.docker.io` | What you need to change |
| --- | --- |
| Argument to `GetCredentialsAsync` | Nothing. Keep `index.docker.io`. |
| Key inside Docker `auths` | Nothing for this alias change. The existing key is still supported. |
| Key inside Docker `credHelpers` | Rename the key to `https://index.docker.io/v1/`. |
| Key inside containers/Podman `credHelpers` | Nothing when calling with `index.docker.io`. Keep the host key. |

For the custom Docker helper example above, replace the `credHelpers` entry with:

```json
{
  "credHelpers": {
    "https://index.docker.io/v1/": "example"
  }
}
```

Keep the helper name (`example`) and your API call unchanged. The call now
selects `docker-credential-example`. The same Docker configuration change
applies to `credHelpers` keys named `docker.io` or `registry-1.docker.io`.
Do not apply this replacement to containers/Podman helper keys.

Docker helpers now receive `https://index.docker.io/v1/` for these Docker Hub
inputs, including when selected by a global `credsStore`. If the helper stores
credentials under the old host key, use its supported login or store procedure
to populate the new key. Update custom helpers and mocks that expect the old
input. The `credsStore` setting itself remains the helper name.

##### Other URL- or repository-qualified credential-helper keys

In Docker or containers `credHelpers`, replace URL- or repository-qualified keys
with the host and any explicit port. For example, rename the configuration key
`https://registry.example.com:5000/team` to `registry.example.com:5000`, keeping
its helper name unchanged. You can still pass
`https://registry.example.com:5000/team` to `GetCredentialsAsync`.

Match key casing exactly. The helper receives `registry.example.com:5000`, not
the original URL. If needed, populate credentials under that key using the
helper's supported login or store procedure and update custom helpers or mocks.
Already matching host-and-port keys need no change.

##### Containers/Podman auth files

If your file uses `"credsStore": "example"`, replace it with explicit helpers
for each registry you use:

```json
{
  "credHelpers": {
    "registry.example.com": "example",
    "docker.io": "example"
  }
}
```

For inline `auths`, make the API argument's host and namespace casing match the
configured key. Change a namespace key such as `registry.example.com/team` to
`registry.example.com` only if those credentials should apply registry-wide.
To cover just the two namespaces in the example, configure separate entries for
`registry.example.com/team` and `registry.example.com/other`. If only the team
namespace should have access, keep its entry unchanged.

If you currently select a Docker configuration through `REGISTRY_AUTH_FILE`
and need Docker rules, unset `REGISTRY_AUTH_FILE` and
set `DOCKER_CONFIG` to the directory containing your Docker `config.json`.
Containers auth files in the normal search locations still take priority.

##### Conflicting inline credentials or invalid inputs

Remove duplicate or equivalent `auths` entries with different credentials;
reordering them no longer controls which credentials are selected. Keep the
intended registry-level credential, plus any deliberate containers namespace
overrides.

Pass a valid hostname or HTTP(S) URL, optionally with a port and repository
path, but without an image tag or digest. For example, replace
`GetCredentialsAsync("registry.example.com/team/image:latest")` with
`GetCredentialsAsync("registry.example.com/team/image")`. Remove whitespace
padding, URL user information, query strings, and fragments; correct invalid
ports or unsupported schemes. If you handle invalid input, expect
`ArgumentException` rather than a missing-credentials error.

After updating, run your actual lookup inputs against a disposable configuration
and verify the expected username. For helpers, verify the received registry key;
for namespace credentials, verify that unrelated repositories do not receive
them. Do not log passwords or tokens.

#### Affected APIs

Both public overloads in `Valleysoft.DockerCredsProvider` are affected:

- `CredsProvider.GetCredentialsAsync(string registry)`
- `CredsProvider.GetCredentialsAsync(string registry, CancellationToken cancellationToken)`

Affected configuration includes `auths`, `credHelpers`, `credsStore`,
`REGISTRY_AUTH_FILE`, and `DOCKER_CONFIG`.

#### References

- [Format-aware registry matching (#95)](https://github.com/mthalman/docker-creds-provider/pull/95)
- [Parent-namespace fallback added to containers lookup (#96)](https://github.com/mthalman/docker-creds-provider/pull/96)
- [Docker credential helper protocol](https://docs.docker.com/reference/cli/docker/login/#credential-helper-protocol)
- [Containers auth file format](https://github.com/containers/image/blob/main/docs/containers-auth.json.5.md)
