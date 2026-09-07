# AGENTS

## Build and test

Run all commands from the `src/` directory:

```shell
dotnet restore
dotnet build -c Release --no-restore
dotnet test --no-restore -v normal -c Release --results-directory test-results -l trx
```

## Architecture

This repository contains the `Valleysoft.DockerCredsProvider` .NET library.
`CredsProvider.GetCredentialsAsync` is the public entry point for resolving
registry credentials from Docker-compatible configuration files.

Credential lookup checks configured auth files in priority order and returns an
`ICredStore` implementation:

- `NativeStore` invokes a configured native credential helper.
- `EncodedStore` reads inline credentials from an `auths` entry.

File-system, process, and environment access use interfaces so tests can replace
external dependencies.

## Conventions

- Preserve support for every target framework declared in the project file.
- Keep nullable reference types and analyzers enabled.
- Add or update tests for behavior changes.
- Update `README.md` when installation, configuration, public API, or error
  behavior changes.

## Pull request labels

Every pull request must have exactly one semantic-version label, selected by the
highest-impact public change:

- `semver:major` for breaking public API or behavior
- `semver:minor` for backward-compatible public functionality
- `semver:patch` for fixes, documentation, dependencies, tests, build changes,
  or maintenance

Apply at most one canonical visible category:

- `enhancement` for features
- `bug` for fixes
- `documentation` for documentation-only changes
- `dependencies` for dependency updates
- No category for maintenance, refactoring, tests, or infrastructure

For mixed pull requests, classify by the highest-impact public change. A
test-heavy pull request that fixes a product bug is `bug`; a dependency pull
request spanning production and tooling dependencies remains `dependencies`.

Apply `skip-changelog` to internal-only test dependency updates, CI action
updates, build or tooling changes, and repository administration that are not
useful to package users. Do not apply it to production dependency updates,
user-facing fixes, features, documentation, or significant release behavior
that users or maintainers should know about.

After creating a pull request, apply the labels on GitHub and verify them before
considering pull request creation complete.
