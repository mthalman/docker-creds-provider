# Contributing

Contributions are welcome. Open an issue to discuss a bug or proposed change,
or submit a pull request with a focused implementation.

## Prerequisites

Install Git and a stable .NET SDK compatible with [`global.json`](global.json).
Download the SDK from the
[.NET download page](https://dotnet.microsoft.com/download).
[`global.json`](global.json) uses `latestFeature` to select the highest
installed compatible feature band and patch.

## Build and test locally

From the repository root, run the same commands used by CI (CI sets `src` as
the working directory). Restore requires access to NuGet.org:

```console
cd src
dotnet restore
dotnet build -c Release --no-restore
dotnet test --no-restore -v normal -c Release --results-directory test-results -l trx
```

To create the same package artifacts validated by CI, continue from `src`:

```console
dotnet pack -c Release --no-build --output package-output Valleysoft.DockerCredsProvider
```

## Submit a pull request

Before opening a pull request:

1. Add or update tests for behavior changes.
2. Update `README.md` when a change affects installation, configuration, the
   public API, or error behavior. For breaking changes, also add a
   [migration fragment](#document-a-breaking-change); keep upgrade instructions out of
   README.md.
3. Run the Release build and test commands above.
4. Keep the pull request focused on one change and explain its user-visible
   effect.

CI builds and tests pull requests on Linux and Windows, and validates package
contents on Linux.

## Document a breaking change

Every pull request labeled `semver:major` must add a new file named
`.changes/+short-description.breaking.md`. Use a unique, lowercase, hyphenated
description; the `+` allows authoring before a PR number exists.
The slug becomes the published topic's filename, so choose a stable description.
Do not use `readme`, which is reserved for each version's topic index.

These fragments are authoring inputs, not the reader-facing documentation.
[Migration guides](docs/migrations/README.md) group individual topic documents
inside version directories, such as
`docs/migrations/3.0.0/credential-helper-errors.md`. Each version has a
`README.md` topic index. Release Drafter generates these guides before
publication and waits for the exact files to merge on `main` before updating
the release draft with linked topic titles. For unpublished changes, edit the
fragment rather than the generated guides. After publication, correct the
versioned topics directly through a reviewed documentation PR.
Use absolute URLs in fragment links so they work in versioned guides.

Start with this structure and replace the example text with specific guidance:

```markdown
### Name the breaking change

Identify the affected consumers and the condition that triggers the change.

#### Previous behavior

Describe the observable behavior before this change.

#### New behavior

Describe the observable behavior after this change, including relevant limits
and behavior that remains unchanged.

#### Type of breaking change

Identify a behavioral, source-incompatible, or binary-incompatible change
(or a combination), and explain its effect on consumers.

#### Reason for change

Explain why the change is necessary despite the compatibility impact.

#### Recommended action

Explain the required consumer changes, with before-and-after code when useful.
If no code change is needed, explain what consumers must verify.
Include a check that confirms the migration worked.

#### Affected APIs

List the public APIs and affected overloads. For a configuration or tooling
change without an affected API, name the affected setting or command instead.
```

Use the six level-four sections above in that order. Within a section, use
level-five or level-six headings for before-and-after examples and fenced code
blocks with a language identifier. Add an optional `#### References` section
with absolute links to the implementing PR, issue, or related documentation
when available. Do not guess a PR number or release version: Release Drafter's
dry-run preview supplies the version context, and generated topic documents
include **Version introduced** from its computed tag. Guides do not link to a
release that does not yet exist. The topic title becomes the concise linked
summary in release notes, so make it meaningful without the topic body.

The **Validate migration notes** check requires a new fragment for major PRs,
rejects `skip-changelog` on those PRs, and validates the filename and required
sections and their order in every added or edited fragment, including on
non-major PRs. It rejects empty or heading-only sections, bare TODO/TBD/N/A
placeholders, and reserved `migration-notes` start/end and `migration-topic`
markers. The base-owned validator also checks the same required sections in
added or edited versioned topics, including published corrections.
Headings inside fenced examples do not count as document sections.
Indexes are navigation pages, not migration topics, and do not use this format.
Guide deletions, including version indexes, are allowed only beneath a version
already listed in `.github/migration-guides.json` under `pending_versions` at the
current PR base commit. Changing that state in the same PR cannot grant deletion
permission. The root migration index cannot be deleted.
Reviewers must still check technical accuracy, compatibility classification,
affected-API coverage, and completeness; tooling does not infer
instructions from code.

The policy runs the validator from the PR's base revision and reads PR commits
as Git data without checking them out or executing their code. Changing the
validator in a PR cannot change the policy applied to that PR.
The separate **Test migration tooling** job tests the proposed tooling changes
and dependencies in the ordinary `pull_request` workflow.

Keep fragments in Git after publication. Do not delete, rename, or reuse them
for a later breaking change. Corrections to existing fragments do not satisfy
the new-fragment requirement for a major PR.

### Correct migration guidance

For an unpublished change, edit its source fragment and merge the correction
into `main`. Rerun **Release Drafter** if it does not start automatically. Review
and merge the updated generated documentation PR, then confirm that Release
Drafter succeeds. The release draft stays unchanged while the required guides
are missing or differ from the generated content.

For a published change, edit its existing versioned topic in
`docs/migrations/<version>/` through a reviewed documentation PR. Published
guides are the authoritative migration details, and later automation preserves
them. Retain each topic's path and fragment slug. If you change a topic title,
also update that version's `README.md` topic index; the root version index is
generated. Keep the required sections complete and verify the correction.
Do not edit the published release or its old source fragment to correct a
published guide. Existing release links to `main` pick up the merged correction.

### Format rationale

This repository adapts the
[.NET breaking-change template](https://github.com/dotnet/docs/blob/main/.github/ISSUE_TEMPLATE/02-breaking-change.yml)
and [compatibility categories](https://learn.microsoft.com/en-us/dotnet/core/compatibility/categories).
The template separates old and new behavior, the reason, consumer action, and
affected APIs. We supply its version field from Release Drafter's preview
instead of asking fragment authors to predict it.

[Google AIP-180](https://google.aip.dev/180) reinforces that observable behavior,
not just API signatures, is part of compatibility.
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) recommends human-readable,
versioned notes that make breaking changes visible. These are established
project and community conventions, not one universal migration-document
standard. GitHub Releases remain this repository's changelog.

### Preview migration notes

[Towncrier](https://towncrier.readthedocs.io/) is release tooling only; it does
not change .NET package versions or publish anything. Install Python 3.13.

Replace `v2.3.0` with the previous published release tag. Commit your fragment
before previewing: the helper reads fragments, Towncrier configuration, and the
template from the selected commit, not uncommitted files.
It renders only fragments absent from that release and present at `HEAD`.
The tag must exist locally and be an ancestor of `HEAD`. Missing or unrelated
release history is an error, not a reason to include old notes.

From the repository root, install the pinned dependency, run the checks, and
render the preview:

```console
python -m pip install -r .github/scripts/requirements.txt
python -B -m unittest discover -s .github/scripts -p test_migration_notes.py
python .github/scripts/migration_notes.py render --base v2.3.0 --head HEAD
```

The helper stages those fragments in a temporary directory and runs Towncrier
in `--draft` mode. It does not delete fragments, create tags, or alter the
working tree. In automation, Release Drafter uses the same selected fragments
to generate versioned topics before publication. Towncrier's Markdown,
including fenced and nested examples, is preserved rather than processed as
a Release Drafter template.

Release Drafter opens or updates a draft documentation PR containing the topic
files, indexes, and `.github/migration-guides.json` state. Its `pending_versions`
list tracks automation-owned unpublished guide directories. Release Drafter
fails with **WAITING FOR MIGRATION GUIDES** until that exact proposal is merged
on `main`, leaving the existing release draft unchanged. After the merge, a
successful run updates the draft with concise linked topic titles, not the full
topic bodies. During version changes, automation retains superseded guides while
any release body links to their `main` URL prefix. Existing draft links stay
usable until the new guides merge and the draft switches links. A later run,
possibly dispatched manually, can propose cleanup through another documentation
PR. Published guides are always retained. See the
[maintainer procedure](MAINTAINERS.md#merge-migration-guides-before-updating-the-draft)
for review, CI, and rerun instructions.
