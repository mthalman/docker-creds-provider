# Migration notes

User-facing migration instructions are maintained as Markdown files in
[`migrations`](migrations). The release workflow combines the notes for each
release into a prominent **Breaking changes and migration** section in its
[GitHub Release](https://github.com/mthalman/docker-creds-provider/releases).

For 3.0.0, see [credential-helper errors](migrations/+credential-helper-errors.breaking.md).

## Document a breaking change

Every pull request labeled `semver:major` must add a new file named
`docs/migrations/+short-description.breaking.md`. Use a unique, lowercase,
hyphenated description; the `+` allows authoring before a PR number exists.
Do not put migration instructions in README.md or edit the GitHub draft by hand.

Start with this structure and replace the example text with specific guidance:

```markdown
### Name the breaking change

#### What changed

Describe the previous behavior, the new behavior, and who is affected.

#### How to migrate

Explain the required consumer changes, with before-and-after code when useful.
If no code change is needed, explain what consumers must verify.
```

The **Validate migration notes** check requires a new fragment for major PRs,
rejects `skip-changelog` on those PRs, and validates the filename and required
sections of every added or edited fragment, including on non-major PRs. It
rejects empty sections and bare TODO/TBD/N/A placeholders. Reviewers must still
check technical accuracy and completeness; tooling does not infer migration
instructions from code.

Keep fragments in Git after publication. Do not delete, rename, or reuse them
for a later breaking change. Corrections to existing fragments do not satisfy
the new-fragment requirement for a major PR.

## Preview the generated notes

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
in `--draft` mode. It does not generate a committed aggregate changelog, delete
fragments, create tags, or alter the working tree.
