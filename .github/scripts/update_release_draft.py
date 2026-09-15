import argparse
import json
import os
import subprocess
from pathlib import Path

from migration_notes import MIGRATION_END, MIGRATION_START, STABLE_TAG, git, markdown_lines, previous_tag, render
from migration_guides import guides_ready, linked_notes, plan_guides, write_guides


def api(endpoint: str, method: str = "GET", payload: dict | None = None):
    args = ["gh", "api", endpoint]
    if payload is None:
        args.extend(["--paginate", "--slurp"])
    else:
        args.extend(["--method", method, "--input", "-"])
    result = subprocess.check_output(
        args, input=json.dumps(payload) if payload is not None else None,
        text=True, encoding="utf-8",
    )
    data = json.loads(result)
    return [release for page in data for release in page] if payload is None else data


def fingerprint(releases: list[dict]) -> list[tuple]:
    fields = ("id", "tag_name", "name", "draft", "prerelease", "target_commitish",
              "updated_at", "published_at", "body")
    return [tuple(release[field] for field in fields) for release in releases]


def combine_notes(preview: str, migrations: str) -> str:
    previous_tag(preview)
    changes = preview.split("-->", 1)[1].lstrip("\r\n")
    if not changes.strip():
        raise ValueError("Release Drafter preview has no release notes.")
    if MIGRATION_START in migrations or MIGRATION_END in migrations:
        raise ValueError("Migration notes contain a reserved release-note marker.")
    if not migrations:
        return changes

    lines = changes.splitlines(keepends=True)
    headings = [
        (index, len(heading[1]), (heading[2] or "").strip())
        for index, (_, heading) in enumerate(markdown_lines(changes)) if heading
    ]
    breaking = [index for index, level, title in headings
                if level == 3 and title == "Breaking Changes"]
    if (headings[:1] != [(0, 2, "What's Changed")] or
            any(level <= 2 for _, level, _ in headings[1:]) or len(breaking) > 1):
        raise ValueError(
            "Unexpected Release Drafter category layout: expected categories beneath "
            "'## What's Changed' and at most one '### Breaking Changes' section."
        )

    block = f"{MIGRATION_START}\n**Migration guides**\n\n{migrations.rstrip()}\n{MIGRATION_END}"
    if breaking:
        insertion = next(
            (index for index, level, _ in headings if index > breaking[0] and level <= 3),
            len(lines),
        )
    else:
        insertion = 1
        block = "### Breaking Changes\n\n" + block
    before = "".join(lines[:insertion]).rstrip()
    after = "".join(lines[insertion:]).lstrip("\r\n")
    return f"{before}\n\n{block}" + (f"\n\n{after}" if after else "\n")


def check_releases(endpoint: str, snapshot: list[dict], name: str, tag: str) -> list[dict]:
    if not name or not STABLE_TAG.fullmatch(tag):
        raise ValueError("Expected a named stable release with a v-prefixed tag.")
    current = api(endpoint)
    if fingerprint(current) != fingerprint(snapshot):
        raise ValueError("Releases changed during generation; rerun Release Drafter.")
    drafts = [release for release in current if release["draft"]]
    if any(release["prerelease"] or not STABLE_TAG.fullmatch(release["tag_name"])
           for release in drafts):
        raise ValueError(
            "Unrelated release drafts found; remove or retag them before rerunning. "
            "Publishing requires exactly one draft."
        )
    if len(drafts) > 1:
        raise ValueError("Multiple stable release drafts found; select one before rerunning.")
    if any(not release["draft"] and release["tag_name"] == tag for release in current):
        raise ValueError("Release Drafter selected an already published version.")
    return drafts


def update_draft(
    endpoint: str, snapshot: list[dict], body: str, name: str, tag: str, commit: str
) -> dict:
    drafts = check_releases(endpoint, snapshot, name, tag)
    payload = {
        "body": body, "name": name, "tag_name": tag, "target_commitish": commit,
        "draft": True, "prerelease": False,
    }
    target = f"{endpoint}/{drafts[0]['id']}" if drafts else endpoint
    result = api(target, "PATCH" if drafts else "POST", payload)
    if any(result[key] != value for key, value in payload.items()) or result["published_at"]:
        raise ValueError("GitHub did not return the expected draft body, tag, and draft status.")
    return result


def verify_main(repo: Path, commit: str) -> None:
    remote = git(repo, "ls-remote", "origin", "refs/heads/main").split()
    if len(remote) != 2 or remote[0] != commit:
        raise ValueError("main changed during generation; rerun Release Drafter.")


def restore_complete_history(repo: Path) -> None:
    if git(repo, "rev-parse", "--is-shallow-repository").strip() == "true":
        print("Restoring full Git history after documentation PR preparation.")
        git(repo, "fetch", "--unshallow", "origin")


def process_preview(
    repo: Path, commit: str, preview: str, name: str, tag: str,
    repository: str, snapshot: list[dict], *, prepare: bool,
) -> bool:
    endpoint = f"repos/{repository}/releases"
    verify_main(repo, commit)
    check_releases(endpoint, snapshot, name, tag)
    if not prepare:
        restore_complete_history(repo)
    base = previous_tag(preview)
    notes = render(repo, f"refs/tags/{base}" if base else None, commit)
    documents = plan_guides(repo, commit, notes, tag, snapshot)
    body = combine_notes(preview, linked_notes(notes, tag, repository))
    ready = guides_ready(repo, commit, documents)
    if prepare:
        verify_main(repo, commit)
        check_releases(endpoint, snapshot, name, tag)
        write_guides(repo, commit, documents)
        return ready
    if not ready:
        raise ValueError(
            "Waiting for migration guides: merge the current documentation PR and rerun "
            "Release Drafter. The existing release draft has not been changed."
        )
    verify_main(repo, commit)
    result = update_draft(endpoint, snapshot, body, name, tag, commit)
    print(f"Updated draft release {result['id']} ({result['tag_name']}) with links to merged guides.")
    return True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("snapshot", "prepare", "update"))
    parser.add_argument("--snapshot", type=Path, required=True)
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args()
    endpoint = f"repos/{os.environ['GITHUB_REPOSITORY']}/releases"
    if args.command == "snapshot":
        args.snapshot.write_text(json.dumps(api(endpoint)), encoding="utf-8")
        return
    repo = Path(git(Path.cwd(), "rev-parse", "--show-toplevel").strip())
    commit = git(repo, "rev-parse", "--verify", f"{args.head}^{{commit}}").strip()
    ready = process_preview(
        repo, commit, os.environ["RELEASE_PREVIEW"],
        os.environ["RELEASE_NAME"], os.environ["RELEASE_TAG"],
        os.environ["GITHUB_REPOSITORY"],
        json.loads(args.snapshot.read_text(encoding="utf-8")),
        prepare=args.command == "prepare",
    )
    if args.command == "prepare":
        if "GITHUB_OUTPUT" in os.environ:
            with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
                output.write(f"guides-ready={str(ready).lower()}\n")
        print("Migration guides match main." if ready else
              "Waiting for migration guides: documentation PR required; release draft unchanged.")


if __name__ == "__main__":
    main()
