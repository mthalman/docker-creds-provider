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
  behavior changes. Put upgrade-specific instructions in migration fragments,
  not README.md.

## Breaking-change migration notes

For every `semver:major` pull request, add a new
`.changes/+short-description.breaking.md` file in the same PR. Follow
[the migration-note format](CONTRIBUTING.md#document-a-breaking-change): document the old and new
behavior under `#### What changed` and actionable consumer instructions under
`#### How to migrate`. Verify the implementation before describing its contract.

Use a unique lowercase slug; the filename does not need a PR number. Keep
released fragments in Git, never rename or reuse them, and do not manually
maintain migration prose in the draft release. Release Drafter receives
Towncrier-generated notes on every run and excludes already released fragments.
Do not label a breaking-change PR `skip-changelog`.

Reader-facing topics live at `docs/migrations/<version>/<fragment-slug>.md`,
with a `README.md` topic index inside each version directory and a version index
in `docs/migrations/README.md`. They are generated from published release notes by
the Migration guides workflow, which opens a draft documentation PR. Do not
edit them independently or put authoring instructions in that directory.
For unpublished changes, edit the fragment; for published corrections, update
the release's migration section and rerun Migration guides. Preserve its
`migration-notes` start/end markers and `migration-topic` slug markers. Topic
filenames come from fragment slugs, not titles; `readme` is reserved for the
version index.

When changing migration tooling, run its tests from the repository root:

```shell
python -m pip install -r .github/scripts/requirements.txt
python -B -m unittest discover -s .github/scripts -p test_migration_notes.py
```

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
