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
`README.md` topic index. Do not manually edit these generated files.
Use absolute URLs in fragment links so they also work in release notes and
versioned guides.

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
when available. Do not guess a PR number or release version: the release
provides the version context, and generated topic documents include
**Version introduced** from the published release tag.

The **Validate migration notes** check requires a new fragment for major PRs,
rejects `skip-changelog` on those PRs, and validates the filename and required
sections of every added or edited fragment, including on non-major PRs. It
rejects empty sections, bare TODO/TBD/N/A placeholders, and reserved
`migration-notes` start/end and `migration-topic` markers. The same section
validation runs on published topics before generating versioned guides.
Indexes are navigation pages, not migration topics, and do not use this format.
Reviewers must still check technical accuracy, compatibility classification,
affected-API coverage, section order, and completeness; tooling does not infer
instructions from code.

Keep fragments in Git after publication. Do not delete, rename, or reuse them
for a later breaking change. Corrections to existing fragments do not satisfy
the new-fragment requirement for a major PR.

### Format rationale

This repository adapts the
[.NET breaking-change template](https://github.com/dotnet/docs/blob/main/.github/ISSUE_TEMPLATE/02-breaking-change.yml)
and [compatibility categories](https://learn.microsoft.com/en-us/dotnet/core/compatibility/categories).
The template separates old and new behavior, the reason, consumer action, and
affected APIs. We supply its version field from release metadata instead of
asking fragment authors to predict it.

[Google AIP-180](https://google.aip.dev/180) reinforces that observable behavior,
not just API signatures, is part of compatibility.
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) recommends human-readable,
versioned notes that make breaking changes visible. These are established
project and community conventions, not one universal migration-document
standard. GitHub Releases remain this repository's changelog.

### Preview migration notes

[Towncrier](https://towncrier.readthedocs.io/) is release tooling only; it does
not change .NET package versions or publish anything. Install Python 3.13 and
the pinned dependency, then run from the repository root:

```console
python -m pip install -r .github/scripts/requirements.txt
python -B -m unittest discover -s .github/scripts -p test_migration_notes.py
python .github/scripts/migration_notes.py render --base v2.3.0 --head HEAD
```

Replace `v2.3.0` with the previous published release tag. Commit your fragment
before previewing: the helper reads the selected commit, not uncommitted files.
It renders only fragments absent from that release and present at `HEAD`.
The tag must exist locally and be an ancestor of `HEAD`. Missing or unrelated
release history is an error, not a reason to include old notes.

The helper stages those fragments in a temporary directory and runs Towncrier
in `--draft` mode. It does not delete fragments, create tags, or alter the
working tree. After publication, automation opens a draft documentation PR
containing individual topic files in the version's directory and updated
indexes, copied from the migration section in the published release notes.
