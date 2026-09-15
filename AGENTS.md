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
behavior in separate `#### Previous behavior` and `#### New behavior` sections,
then include `#### Type of breaking change`, `#### Reason for change`,
`#### Recommended action`, and `#### Affected APIs` in that order. Identify
affected consumers, verify both previous and current behavior, classify the
compatibility impact, and give actionable migration and verification steps.
Specify affected overloads; use a setting or command for non-API changes.
Include before-and-after examples and reference links when useful. Follow
the linked .NET-based format, not a PR summary or commit log.

Use a unique lowercase slug; the filename does not need a PR number. Keep
released fragments in Git, never rename or reuse them, and do not manually
maintain migration prose in the draft release. Release Drafter renders selected
fragments with Towncrier into versioned guides before updating the draft and
excludes already released fragments.
Do not label a breaking-change PR `skip-changelog`.

Reader-facing topics live at `docs/migrations/<version>/<fragment-slug>.md`,
with a `README.md` topic index inside each version directory and a version index
in `docs/migrations/README.md`. Release Drafter is the single automation owner:
one dry-run preview supplies the computed tag and previous release boundary.
Generated guides get **Version introduced** from that computed tag; never
predict a version in a fragment. Topic filenames come from fragment slugs, not
titles; `readme` is reserved for the version index.

The exact generated topics, indexes, and `.github/migration-guides.json` state
must be merged on `main` before Release Drafter can update the release draft.
Otherwise, it opens or updates the draft PR on `automation/migration-guides`
with `GITHUB_TOKEN`, fails with **WAITING FOR MIGRATION GUIDES**, and leaves the
existing release draft unchanged. The PR uses `draft: always-true`,
`semver:patch`, and `documentation`, without `skip-changelog`. Review it, mark it
ready for review, run the required checks, and merge it. Confirm the next
Release Drafter run succeeds; dispatch it manually if the merge does not
trigger a run. Automation never writes directly to `main` or merges PRs.
Successful drafts place concise linked topic titles inside the `Breaking Changes`
category under `What's Changed`. All categories are child headings of
`What's Changed`. Links point to committed guides on `main`, not future tags or
unmerged files. Never tag a release based on a waiting or stale draft; the
publishing workflow does not enforce this gate.

For unpublished corrections, edit the source fragment and rerun Release
Drafter. Do not edit unpublished generated guides independently. The metadata
tracks automation-owned unpublished directories in `pending_versions`. Version
changes reuse the documentation PR and wait for the new guides and state to
merge before switching draft links. Retain a superseded pending directory while
any release body contains its `main` guide URL prefix, so unchanged draft links
stay usable. Unlinked superseded proposals can be removed immediately.
After switching links, a later Release Drafter run can propose cleanup in
another documentation PR; this may require a manual run and another merge.
Once a tag is published, automation preserves that version's directory and
removes it from `pending_versions`. Published guides are the authoritative
migration details: correct them through reviewed documentation PRs, preserving
paths and slugs. Update the version's topic index when changing a published
topic title. The PR policy permits guide deletions only beneath versions listed
in `pending_versions` at the current PR base commit, not state added in the PR
or an older merge base. Keep the root migration index.
Do not correct published releases or old fragments, and do not
reintroduce released fragments. The root index is generated and includes
upcoming guides without implying publication. Releases without existing guides
are not backfilled from release bodies. There is no post-publication archive
workflow. Do not put authoring instructions in `docs/migrations/`.

Keep policy enforcement separate from tooling tests. The **Migration note
policy** workflow uses `pull_request_target` and executes only the validator
from the base commit. Fetch PR commits as Git data, but never check out or
execute PR code or install PR dependencies in that job. The same six ordered
sections are validated in fragments and versioned topics; navigation indexes
are exempt. Proposed tooling and dependency changes are tested separately
through `pull_request`.

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
