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

### Enable migration automation

Complete this setup to enforce migration notes and generate documentation PRs:

1. Merge the migration workflows into `main`. The policy workflow uses
   `pull_request_target`, so it cannot run from an unmerged setup PR.
2. Enable **Allow GitHub Actions to create and approve pull requests** in the
   repository's Actions settings. No additional token is needed.
3. Trigger a PR event, such as a label change or code update, and confirm that
   **Validate migration notes** runs. Existing PRs need a new event after the
   policy workflow is available on `main`.
4. Require the **Validate migration notes** status check in the `main` branch
   ruleset so a `semver:major` PR cannot merge without its migration fragment.
5. Run **Release Drafter**, review and merge its generated documentation PR,
   and rerun Release Drafter until it successfully writes the linked draft.
   Complete this rollout before creating a release tag.

At rollout, the existing v3 draft contains migration guidance for #93 only;
#95 and #97 do not have migration topics. This workflow change does not supply
those missing topics or establish complete migration coverage. Review coverage
separately before releasing; do not treat an unchanged legacy draft as a
successful rollout.

### Automate breaking-change migration notes

The **Migration note policy** workflow reruns on label changes as well as code
changes. It uses `pull_request_target` so the workflow itself is trusted, checks
out the PR's base commit, and fetches the head commit only as Git data. It runs
the base validator in Python isolated mode with no dependency installation.
The checkout retains read-only authentication for the fetch; PR code is never
checked out or executed in this job. Do not add PR builds, tests, or dependency
installation to it.

The separate **Test migration tooling** job uses the ordinary `pull_request`
workflow to exercise the proposed scripts and dependencies, without persisted
checkout credentials. See
[migration-note authoring](CONTRIBUTING.md#document-a-breaking-change) for the
required format.

Each topic follows the .NET-based format documented in CONTRIBUTING.md:
previous behavior, new behavior, type of breaking change, reason for change,
recommended action, and affected APIs. Review the compatibility classification,
the affected overloads or settings, and the consumer's verification steps;
section validation cannot establish technical accuracy.
The same base-owned validator checks added or edited versioned topics;
navigation indexes are exempt.

Release Drafter is the single owner of guide generation and release drafting.
The workflow runs one read-only preview to obtain both the computed tag and
`$PREVIOUS_TAG`. This uses the same release boundary for the changelog, version
resolution, and fragment selection. The helper selects fragments added since
that tag at the selected `main` commit, validates them, and renders them with
Towncrier. For a first release, all committed fragments are included.

Towncrier's literal Markdown becomes versioned topic documents, not inline
release prose. Migration text is not processed as a Release Drafter template:
fenced and nested examples, including variables such as `$OWNER`, remain
unchanged. Once the exact generated files and state are committed on `main`,
the helper prepends concise bullet links to Release Drafter's categorized body.
Each topic title serves as its summary. Links use
`https://github.com/<repo>/blob/main/docs/migrations/<computed-version>/<slug>.md`,
never a future tag or an unmerged file. The helper then creates or updates an
unpublished draft through the GitHub API.

The workflow snapshots release metadata before the preview and rechecks both
the release snapshot and remote `main` before preparing working files and again
before writing the draft. If a release is published, a draft changes, or remote
`main` advances during generation, it fails and must be rerun. Multiple stable
drafts also fail instead of silently choosing one. A failed preview, missing
history, invalid fragment, or failed render stops the workflow before it writes
a draft. Before writing,
the helper rereads the selected `main` commit's Git objects, compares the exact
required guide files, indexes, and state, and verifies that remote `main` still
points to that commit. A mismatch stops the draft update. Runs are serialized
and check out current `main` so queued runs do not render an older push. Avoid
publishing or manually editing releases while drafting runs.

The documentation PR action can shallow-fetch its automation branch. Before
the final render, the updater restores full Git history if necessary, without
changing the selected commit. A failed fetch stops the update; the previous
release must still be an ancestor of that commit.

This integration drafts stable, `v`-prefixed releases, as configured today.
Prerelease drafts and drafts whose tags are not stable `vMAJOR.MINOR.PATCH`
versions are left untouched, but block draft generation: the publishing
workflow requires exactly one draft release. Resolve unrelated drafts before
rerunning Release Drafter. Supporting a separate prerelease draft stream
requires updating both draft selection and publishing alongside Release
Drafter's configuration.

After a release is published, fragments present at its tag are automatically
excluded from the next draft. No fragment cleanup or manual reapplication of
migration notes is needed. The fragments remain available in Git. Published
GitHub Releases remain the changelog; committed versioned guides are the
authoritative migration details.

Manual additions to the draft body are still overwritten. Make unpublished
migration corrections in their source fragments and merge them into `main`.
The merge triggers **Release Drafter**, which can require another documentation
PR before updating the draft. Manual runs also read `main`, not an unmerged branch.
This automation does not create tags, publish releases, or change MinVer's
version calculation.

### Merge migration guides before updating the draft

Authors maintain fragments in `.changes/`. Readers use
[`docs/migrations/README.md`](docs/migrations/README.md), which links to version
directories. Each directory contains a `README.md` topic index and one document
per migration topic, such as
`docs/migrations/3.0.0/credential-helper-errors.md`.
Each generated topic records **Version introduced** from the preview's computed
tag, without linking to a nonexistent release. Authors do not choose a version
directory or duplicate the version in their fragments. The root index includes
upcoming guides without describing them as published.

If the required topics, indexes, and state do not exactly match the selected
`main` commit, Release Drafter opens or updates one draft documentation PR on
`automation/migration-guides`. The PR changes `docs/migrations/` and
`.github/migration-guides.json`, whose `pending_versions` list tracks
automation-owned unpublished guide directories. The PR uses
`GITHUB_TOKEN` and the labels `semver:patch` and `documentation`, without
`skip-changelog`. Release Drafter then explicitly fails with
**WAITING FOR MIGRATION GUIDES** and leaves the existing release draft unchanged.
That draft can still contain old inline migration text or a stale version.

**Do not create a release tag while Release Drafter is waiting or its draft is
stale.** The publishing workflow is unchanged and does not enforce this
merged-guide gate. The maintainer must verify a successful drafting run before
tagging.

Complete the review and merge cycle:

1. Review the generated documentation PR. Check the computed version, topic
   coverage, consumer guidance, and links.
2. Mark the PR ready for review to trigger CI and migration validation through
   `ready_for_review`. If GitHub requests approval, select **Approve workflows
   to run**.
3. Merge the documentation PR after its required checks pass.
4. Confirm that the next **Release Drafter** run succeeds. If the merge uses
   `GITHUB_TOKEN` and does not trigger a run, dispatch Release Drafter manually.
5. Check that the updated release draft proposes the intended version and that
   its migration links resolve to the reviewed guides committed on `main`.
   Only then proceed to [publish a release](#publish-a-release).

PRs created or updated with `GITHUB_TOKEN` can start approval-required workflow
runs for the `opened`, `synchronize`, and `reopened` events. A maintainer can select
**Approve workflows to run** on the PR; see
[GitHub's workflow-triggering rules](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).

Generated PRs use `draft: always-true`. Marking a PR ready for review also
triggers CI and migration validation through their `ready_for_review` event.
An automated update returns it to draft for another review.
The workflow never commits directly to `main` or merges the PR.

If the computed version changes while the documentation PR is open, automation
reuses that PR for the new version. If the superseded proposal already merged,
the next documentation PR generates the new version. Automation retains a
superseded pending directory while any release body contains its `main` guide
URL prefix. This keeps existing draft links usable while Release Drafter waits
for the exact new guides and state to merge. Only after that merge does a
successful run switch the draft to the new links.

On a later Release Drafter run, superseded pending directories no longer linked
from any release body can be removed through another documentation PR. Dispatch
that run manually if necessary, then review and merge the cleanup PR and
confirm Release Drafter succeeds. A superseded proposal that was never linked
can be removed immediately as part of preparing the new version.

Automation removes directories only when they are tracked in `pending_versions`,
unpublished, and no longer linked from any release body. When a tag appears in
the full published-release snapshot, automation preserves that version's
directory and removes it from `pending_versions`. Published directories and
corrections survive later runs; old released fragments are not reintroduced.
Release bodies determine whether pending directories still have links, not
whether a version is published, and never supply topic content. Older releases
without guides are not backfilled. There is no separate post-publication
Migration guides workflow.

The PR policy also rejects guide deletions unless the version is listed in
`pending_versions` at the current PR base commit. This includes version indexes;
the root migration index cannot be deleted. Adding pending state in the cleanup
PR or relying on an older merge base does not authorize deletion. Publication
and live-link checks remain the generation workflow's responsibility.

### Correct migration guides

For an unpublished change, correct its source fragment and merge the correction
into `main`. Rerun Release Drafter and complete the documentation review and
merge cycle above. Do not independently edit unpublished generated topics or
manually maintain migration prose in the draft release.

For a published change, correct the existing versioned topic through a reviewed
documentation PR. Keep its path and fragment slug stable so release links remain
valid. If its title changes, also update that version's `README.md` topic index:
automation preserves published version files instead of regenerating them.
The root version index remains generated. Keep the same six completed sections;
the base-owned **Validate migration notes** check validates edited versioned
topics, while indexes remain exempt.

Do not edit published releases or old fragments to correct a published guide.
Release links point to `main` and pick up the reviewed correction after merge.
No archive or release-body regeneration is needed.

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
version. Require a successful Release Drafter run after any required migration
documentation PR merges, and verify its links resolve to the expected guides on
`main`. Do not tag from a draft left unchanged by **WAITING FOR MIGRATION
GUIDES** or any failed drafting run. The publishing workflow does not check
this gate for you.

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
