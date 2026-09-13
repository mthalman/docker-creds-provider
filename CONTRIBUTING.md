# Contributing

Contributions are welcome. Open an issue to discuss a bug or proposed change,
or submit a pull request with a focused implementation.

## Prerequisites

Install Git and a stable .NET SDK compatible with [`global.json`](global.json).
Download the SDK from the
[.NET download page](https://dotnet.microsoft.com/download).

## Build and test locally

From the repository root, run:

```console
cd src
dotnet restore
dotnet build -c Release --no-restore
dotnet test --no-restore -v normal -c Release --results-directory test-results -l trx
```

To create package artifacts, continue from `src`:

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
3. Keep the pull request focused on one change and explain its user-visible
   effect.

## Document a breaking change

Write migration guidance in `.changes/+short-description.breaking.md`.
The `+` allows authoring before a PR number exists.
The slug becomes the published topic's filename, so choose a stable description.

These fragments are authoring inputs, not the reader-facing documentation.
[Migration guides](docs/migrations/README.md) group individual topic documents
by release. For unpublished changes, edit the fragment rather than the generated
guides. After publication, correct the versioned topics directly through a
reviewed documentation PR.
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

Use fenced code blocks with a language identifier for examples. Add an optional
`#### References` section with absolute links to the implementing PR, issue, or
related documentation when available. Do not guess a PR number or release
version. Make the topic title meaningful without the body so readers can decide
whether the change applies to them.

Reviewers must check technical accuracy, compatibility classification,
affected-API coverage, and completeness; tooling does not infer
instructions from code.

When changing migration tooling, merge validator support for a new generated
topic format into `main` before regenerating the documentation PR in that format.

### Correct migration guidance

For an unpublished change, edit its source fragment and merge the correction
into `main`. Rerun **Release Drafter** if it does not start automatically. Review
and merge the updated generated documentation PR, then confirm that Release
Drafter succeeds.

For a published change, edit its existing versioned topic in
`docs/migrations/<version>/` through a reviewed documentation PR. If you change a
topic title, also update that version's `README.md` topic index. Verify the
correction.
Do not edit the published release or its old source fragment to correct a
published guide. Existing release links to `main` pick up the merged correction.

### Format rationale

This repository adapts the
[.NET breaking-change template](https://github.com/dotnet/docs/blob/main/.github/ISSUE_TEMPLATE/02-breaking-change.yml)
and [compatibility categories](https://learn.microsoft.com/en-us/dotnet/core/compatibility/categories).
The template separates old and new behavior, the reason, consumer action, and
affected APIs.

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

From the repository root, install the pinned dependency, run the checks, and
render the preview:

```console
python -m pip install -r .github/scripts/requirements.txt
python -B -m unittest discover -s .github/scripts -p test_migration_notes.py
python .github/scripts/migration_notes.py render --base v2.3.0 --head HEAD
```

See the
[maintainer procedure](MAINTAINERS.md#merge-migration-guides-before-updating-the-draft)
for review, CI, and rerun instructions.
