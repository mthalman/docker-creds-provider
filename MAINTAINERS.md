# Maintainers

## Label pull requests

Apply exactly one semantic-version label based on the highest-impact public
change:

- `semver:major` for breaking public API or behavior
- `semver:minor` for backward-compatible public functionality
- `semver:patch` for fixes, documentation, dependencies, tests, build changes,
  or maintenance

Apply at most one release-note category:

- `enhancement` for features
- `bug` for fixes
- `documentation` for documentation-only changes
- `dependencies` for dependency updates
- No category for maintenance, refactoring, tests, or infrastructure

For mixed pull requests, classify by the highest-impact public change. For
example, a test-heavy pull request that fixes a product bug is a `bug`. A
dependency pull request that updates production and tooling dependencies remains
a `dependencies` change.

Apply `skip-changelog` to internal-only test dependency updates, CI action
updates, build or tooling changes, and repository administration that package
users do not need to know about. Do not apply `skip-changelog` to production
dependency updates, user-facing fixes, features, documentation, or significant
release behavior.

## Versioning and releases

Package and assembly versions are derived from Git tags by
[MinVer](https://github.com/adamralph/minver). The release workflow starts when
you push a tag:

- Stable release: `v1.2.3`
- Prerelease: `v1.2.3-preview.1`

The `v` prefix is omitted from the resulting package version. For example,
`v1.2.3` produces `Valleysoft.DockerCredsProvider.1.2.3.nupkg`.

Untagged commits use MinVer's deterministic development version. After a stable
release, MinVer increments the patch version and adds an `alpha.0` prerelease
identifier and the Git commit height, so an untagged build cannot be mistaken
for a stable release.

## Manage release notes

[Release Drafter](https://github.com/release-drafter/release-drafter) collects
pull requests merged to `main` in an unpublished GitHub Release and uses their
labels to organize the release notes and select the next version.

If no semantic-version label is present, Release Drafter proposes a patch
release. If more than one is present, the highest version change wins. Pull
requests without a category appear under Maintenance.

Published GitHub Releases are the release-note system of record; this repository
does not maintain a `CHANGELOG.md`.

### Automate breaking-change migration notes

Require the **Validate migration notes** status check in the `main` branch
ruleset so a `semver:major` PR cannot merge without its migration fragment.
The workflow reruns on label changes as well as code changes. See
[migration-note authoring](CONTRIBUTING.md#document-a-breaking-change) for the
required format.

The Release Drafter workflow first runs a read-only preview containing
`$PREVIOUS_TAG`. That is the same release boundary used for its changelog and
version resolution. The helper
selects fragments added since that tag, validates them, and renders them with
Towncrier. For a first release, all committed fragments are included.

The helper prepends Towncrier's literal Markdown to Release Drafter's rendered
body, then creates or updates an unpublished draft through the GitHub API.
Migration text is not processed as a Release Drafter template, so code examples
containing variables such as `$OWNER` remain unchanged. Every run regenerates
both the migration section and the normal categorized notes.

The workflow snapshots release metadata before the preview and rechecks it
immediately before writing. If a release is published or a draft changes during
generation, it fails and must be rerun. Multiple stable drafts also fail instead
of silently choosing one. A failed preview, missing history, invalid fragment,
or failed render stops the workflow before it writes a draft. Runs are
serialized and check out current `main` so queued runs do not render an older
push. Avoid publishing or manually editing releases while drafting runs.

This integration drafts stable, `v`-prefixed releases, as configured today.
Supporting a separate prerelease draft stream requires updating the draft
selection policy alongside Release Drafter's configuration.

After a release is published, fragments present at its tag are automatically
excluded from the next draft. No fragment cleanup or manual reapplication of
migration notes is needed. The fragments remain available in Git; published
release notes remain the record for that version. Changes to old fragments do
not update published releases automatically.

Manual additions to the draft body are still overwritten. Make migration
corrections in their source fragments and rerun **Release Drafter** (or merge
the correction to trigger it). Before tagging, confirm the final draft workflow
completed successfully and contains the expected notes. This automation does
not create tags, publish releases, or change MinVer's version calculation.

### Archive guides by release version

Authors maintain fragments in `.changes/`. Readers use
[`docs/migrations/README.md`](docs/migrations/README.md), which links to one guide
per release version, such as `docs/migrations/3.0.0.md`.

The **Migration guides** workflow reads the marked migration section from each
published stable release, generates the versioned files and index, and opens or
updates one draft documentation PR on `automation/migration-guides`. Only
`docs/migrations/` is committed. Versions without migration instructions are
omitted, and previously archived guides are retained. Repeated runs produce no
changes unless published migration text or the set of releases changed.

The workflow runs on publication, after the Release workflow completes, and
through manual dispatch. The `workflow_run` trigger covers releases published
using `GITHUB_TOKEN`, which do not trigger another `release` event workflow.
It also checks after failed Release runs because publication may have succeeded
before a later package-attachment step failed. It reads only published release
metadata and checks out trusted `main`; it never executes the release tag or
downloads triggering-run artifacts.

Enable **Allow GitHub Actions to create and approve pull requests** in the
repository's Actions settings. No additional token is needed. The built-in
token does not trigger CI when it creates or updates a PR, so generated PRs use
`draft: always-true`. Mark the PR ready for review to trigger CI and migration
validation; an automated update returns it to draft for another review.
Merge the PR after its checks pass. The workflow never commits directly to
`main` or merges the PR.

Published GitHub Releases remain the source of truth for archived guides.
For corrections, edit that release's migration section while preserving its
`<!-- migration-notes:start -->` and `<!-- migration-notes:end -->` markers,
then rerun Migration guides. Do not separately edit generated guides or the
index. Releases predating this system without markers are left alone; malformed
markers fail generation rather than producing an incomplete guide.

## Configure trusted publishing

Complete this setup before pushing the first release tag:

1. Create a GitHub Actions environment named `nuget.org`.
2. Configure required reviewers or other deployment protection rules. Allow
   deployments from the intended `v*` tags, and restrict who can create, update,
   or delete release tags through repository rulesets.
3. Confirm that the NuGet.org account `thalman` owns, or has permission to
   publish, `Valleysoft.DockerCredsProvider`. The workflow's `NuGet/login` action
   uses this account.
4. Add a [NuGet.org trusted-publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
   with these values:

   | Setting | Value |
   | --- | --- |
   | Repository owner | `mthalman` |
   | Repository | `docker-creds-provider` |
   | Workflow file | `release.yml` (filename only) |
   | Environment | `nuget.org` |
   | Package scope | `Valleysoft.DockerCredsProvider` |

   Select the package owner and allow publication of new versions. The
   environment in the policy must match the GitHub environment.
5. After configuring trusted publishing, remove the obsolete
   `NUGET_ORG_API_KEY` repository secret.

Only the protected publishing job can request an OpenID Connect (OIDC) token.
`NuGet/login` exchanges that token for a short-lived API key immediately before
the package pushes. No long-lived NuGet API key is needed.

## Publish a release

Before starting, complete the trusted-publishing setup and confirm that the
Release Drafter draft contains the intended changes and proposes the correct
version.

1. Create the tag on the intended release commit:

   ```shell
   git tag v1.2.3 <commit>
   ```

2. Push the tag:

   ```shell
   git push origin v1.2.3
   ```

3. Approve the deployment to `nuget.org` if GitHub requests approval.
4. Confirm that the workflow succeeds, NuGet.org lists the intended version
   and accepts its symbols, and the corresponding GitHub Release contains
   both the `.nupkg` and `.snupkg` attachments.

The workflow builds and tests the tagged commit, packs once without rebuilding,
and requires exactly one package and one symbol package whose filenames match
the tag. It retains the package and symbols as workflow artifacts for one day.

The protected publishing job downloads those artifacts without rebuilding. It
reuses an existing published GitHub Release for the tag, or requires exactly one
draft release. After pushing to NuGet.org, it publishes that draft with the
release tag and attaches the package and symbols. NuGet pushes skip duplicates,
and GitHub attachment uploads replace same-named assets on reruns.

Tags such as `v1.2.3-preview.1` produce prerelease GitHub Releases that are not
marked latest. Stable releases are marked latest. Rerunning an already
published release does not change its notes or latest status.
