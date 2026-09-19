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

For mixed pull requests, classify by the highest-impact public change. A
test-heavy product bug fix is `bug`; a dependency update spanning production
and tooling dependencies is `dependencies`.

Apply `skip-changelog` to internal-only test dependency updates, CI action
updates, build or tooling changes, and repository administration that package
users do not need to know about. Do not apply it to production dependency
updates, user-facing fixes, features, documentation, significant release
behavior, or a breaking-change PR.

## Versioning and releases

[MinVer](https://github.com/adamralph/minver) derives package and assembly
versions from Git tags. The release workflow supports **stable releases only**:
`v1.2.3` produces `Valleysoft.DockerCredsProvider.1.2.3.nupkg`.
Prerelease tags such as `v1.2.3-preview.1` are rejected before building or
publishing. Untagged builds still use MinVer's deterministic development
versions; this restriction does not change the library or local packing.

Use the exact stable tag and commit prepared by Release Drafter. Tagging an
arbitrary commit or reusing an unprepared legacy draft no longer publishes a
release. See [Publish a release](#publish-a-release).

## Manage release notes

The repository uses `mthalman/release-automation` **v1.0.1**, pinned to
`90551757fe8b061d4dff1a4cab12f10e58f07201`, with its default paths, labels, and
category titles. No consumer configuration file is needed. The toolkit owns
Release Drafter configuration, Towncrier assets, and Python implementation;
do not restore local copies.

The integration consists of:

| File | Responsibility |
| --- | --- |
| `.github/workflows/migration-note-policy.yml` | Call trusted migration policy on PR events |
| `.github/workflows/release-drafter.yml` | Call guide generation and draft preparation on `main` or manual dispatch |
| `.github/workflows/release.yml` | Validate a pushed tag, build/test/pack, publish to NuGet, upload assets, and finalize the GitHub Release |

Published GitHub Releases are the changelog. Versioned topics in
`docs/migrations/` are the authoritative migration details. The toolkit's
[maintainer guide][toolkit-maintainers] describes draft selection, release
boundaries, history retention, and corrections.

### Enable migration automation

Complete rollout through the repository's normal review process:

1. Allow the pinned reusable workflows and Actions in repository/organization
   Actions policy. Enable **Allow GitHub Actions to create and approve pull
   requests**. The default labels listed above must exist.
2. Remove the obsolete **Test migration tooling** requirement from the `main`
   ruleset before merging the workflow removal; otherwise the missing check
   blocks merging. Keep the .NET checks.
3. Merge the callers into `main`. `pull_request_target` uses the base workflow;
   an unmerged onboarding PR cannot demonstrate the new policy.
4. Trigger a PR event and inspect the actual nested check name for the shared
   **Validate migration notes** job. In the `main` ruleset, replace the old
   standalone **Validate migration notes** requirement with that observed
   name. Do not guess the nested name or remove the policy requirement without
   adding its replacement.
5. Verify that `semver:major` without a new valid fragment fails and that
   combining `semver:major` with `skip-changelog` fails.
6. Run **Release Drafter** and complete the documentation review cycle below.
   A successful run must refresh the draft's preparation metadata before the
   first tag is pushed, including when adopting an existing draft.
7. Verify publication separately in a test repository using the toolkit's
   [installation checks][toolkit-publishing]. Exercise rejected tags, failed
   consumer steps, already-published reruns, and the shared concurrency queue.

Workflow files and local tests do not prove live activation. Confirm repository
permissions, nested required checks, environment approvals, the generated PR's
CI path, and a successful post-merge drafting run.

### Automate breaking-change migration notes

The **Migration note policy** caller uses `pull_request_target`. Its callee
executes a pinned toolkit validator with configuration and deletion-authorizing
state from the PR base. The PR head is fetched as Git data; PR code and
dependencies are never executed or installed in this job.

Toolkit behavior is tested upstream; there is no separate migration-tooling
test workflow in this repository. Local consumer integration tests remain
available. See [CONTRIBUTING.md](CONTRIBUTING.md#document-a-breaking-change)
for the six-section fragment format, local tests, and preview instructions.

Release Drafter selects the latest `main` snapshot, resolves one version and
previous-release boundary, and generates guides from retained fragments.
All draft categories are child headings of **What's Changed**. Breaking-change
entries link to committed topics on `main`, not future tags or unmerged files.
Drafting never creates tags or publishes releases.

### Merge migration guides before updating the draft

The default paths remain `.changes/`, `docs/migrations/`, and
`.github/migration-guides.json`. Preserve existing fragments, guides, indexes,
and the state's `pending_versions`; onboarding does not reset them.

When generated files differ from `main`, drafting opens or updates
`automation/migration-guides` as a draft PR with `semver:patch` and
`documentation`, without `skip-changelog`. It then fails at **Wait for merged
migration guides**, leaving the existing release draft unchanged.

1. Review the generated topics, version, coverage, indexes, and state.
2. Have a human mark the PR ready for review. Automation updates return it to
   draft. Product CI and policy callers include `ready_for_review`; approve
   workflow runs if GitHub requests approval.
3. Merge after the required checks pass.
4. Confirm that **Release Drafter** succeeds afterward. Dispatch it manually
   if the merge does not trigger a run.
5. Inspect the prepared draft and verify its links resolve to the expected
   guides on `main`. Do not tag from an unchanged or stale draft.

Published guides and retained source fragments stay in Git. Automation only
proposes deletion of unlinked, unpublished versions recorded in
`pending_versions`; published directories and corrections are retained.
Superseded pending versions still linked by a release body remain until links
switch. A later run and documentation PR may be needed for cleanup.
The PR policy authorizes deletions from state at the current PR base, never
from state introduced in the cleanup PR. Keep the root migration index.

### Correct migration guides

For unpublished changes, edit source fragments and merge them into `main`.
Rerun drafting and complete any required documentation PR cycle. Do not edit
unpublished generated topics independently or hand-edit preparation metadata.

For published changes, correct the existing versioned topic through a reviewed
documentation PR. Preserve paths and slugs. Update that version's `README.md`
index if its topic title changes. Do not correct old fragments or published
release bodies; existing links to `main` pick up the corrected topic.

### Upgrade the shared toolkit

Follow the [toolkit upgrade procedure][toolkit-upgrading]. Verify a published
stable tag's actual commit SHA, then update both reusable workflow references
and both publication Action references together. Keep full 40-character SHA
pins and matching release-tag comments.

Renovate groups these four entrypoints as `release-automation`. Grouping does
not prove compatibility or guarantee a complete upgrade. Review every pin and
update the SHA-pinned links in `AGENTS.md`, `CONTRIBUTING.md`, and this document
in the same PR. Run the consumer integration tests.

Keep `release-drafter`, `cancel-in-progress: false`, and `queue: max` on the
whole tag workflow. The drafting callee owns the same queue; do not duplicate
its lock in the draft caller. GitHub's queue has a 100-pending-run limit.
Refresh draft metadata with a successful upgraded drafting run before tagging.

## Configure trusted publishing

Complete this setup before pushing a release tag:

1. Create a GitHub Actions environment named `nuget.org`.
2. Configure required reviewers or other deployment protection rules. Allow
   deployments from intended stable `v*` tags and restrict release-tag
   creation, updates, and deletion with repository rulesets.
3. Confirm that the NuGet.org account `thalman` owns, or can publish,
   `Valleysoft.DockerCredsProvider`.
4. Add a [NuGet.org trusted-publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
   with these values:

   | Setting | Value |
   | --- | --- |
   | Repository owner | `mthalman` |
   | Repository | `docker-creds-provider` |
   | Workflow file | `release.yml` (filename only) |
   | Environment | `nuget.org` |
   | Package scope | `Valleysoft.DockerCredsProvider` |

   Select the package owner and allow publication of new versions.
5. After configuring trusted publishing, remove the obsolete
   `NUGET_ORG_API_KEY` repository secret.

Only the protected publishing job receives `id-token: write`. It uses
`NuGet/login` to obtain a short-lived API key immediately before the package
push. The preparation-only job also receives `contents: write` for draft
visibility, but runs only the pinned toolkit Action, without a consumer
checkout or OIDC. Consumer restore/build/test/pack runs in a separate job with
only `contents: read`, no environment, and no OIDC permission.

The shared Actions use `GITHUB_TOKEN`. Verify that it can see the prepared
draft. GitHub can require workflow-modification authorization to publish an
older target after default-branch workflow changes; `GITHUB_TOKEN` cannot
receive that authorization. Follow the toolkit's
[credential requirements][toolkit-publishing] if this occurs. Investigate the
permission failure rather than moving a tag or bypassing validation.

## Publish a release

Complete trusted-publishing setup and the successful drafting/review cycle
first. The release workflow checks preparation before external publication
and rechecks it before finalizing, but NuGet and GitHub publication are not one
atomic transaction.

1. Inspect the prepared draft:

   ```shell
   gh api repos/mthalman/docker-creds-provider/releases --paginate --jq '.[] | select(.draft) | {tag_name, target_commitish, html_url}'
   ```

   Expect exactly one draft. Review its stable tag, full prepared commit SHA,
   release notes, CI results, and migration links.
2. Create and push that exact tag at that exact commit, not the current `main`
   tip. Substitute both values below with the draft's values:

   ```shell
   git tag v1.2.3 <prepared-commit-sha>
   git push origin v1.2.3
   ```

3. After the read-only build succeeds, approve deployment to `nuget.org` if
   requested. Publication revalidates preparation after approval.
4. Confirm that NuGet.org lists the intended version and accepts its symbols,
   and that the published GitHub Release has both package attachments.

The preparation-only job calls `prepare-release`. The read-only build job
checks out its validated source with full history, builds/tests/packs, and
requires exactly one `.nupkg` and one `.snupkg` matching the prepared version.
It retains these as a workflow artifact for one day and passes its immutable
artifact ID to the publishing job.

After environment approval, the publishing job calls `prepare-release` again
and requires its context to match the original preparation. It downloads only
that artifact ID, without checking out or executing consumer source, pushes
to NuGet, and attaches both files to the existing draft. It then calls
`finalize-release` with the unmodified revalidated context. Finalization
preserves the prepared release's notes and title. If preparation changes,
review the release and rerun preparation and build rather than publishing
artifacts from a different preparation.

The entire tag workflow shares drafting's concurrency queue, including while
waiting for environment approval. Do not leave an approval pending when a
drafting run must proceed.

For an already-published prepared release, rerunning the original tag-creation
run skips the build, NuGet login/push, uploads, and finalization. An old release
without preparation metadata is not an eligible no-op rerun. If NuGet succeeds
but a later step fails, inspect both services and rerun the original run when
safe: NuGet skips duplicates and attachment uploads replace same-named files.
There is no rollback of external effects. Never delete/recreate or force-move
a tag to repair a failed gate.

[toolkit-maintainers]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/maintainer-guide.md
[toolkit-upgrading]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/upgrading.md
[toolkit-publishing]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/tag-publishing.md
