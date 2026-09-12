import argparse
import json
import os
import subprocess
from pathlib import Path

from migration_notes import MIGRATION_END, MIGRATION_START, STABLE_TAG, git, previous_tag, render


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
    return (
        f"{MIGRATION_START}\n{migrations.rstrip()}\n{MIGRATION_END}\n\n"
        if migrations else ""
    ) + changes


def update_draft(
    endpoint: str, snapshot: list[dict], body: str, name: str, tag: str, commit: str
) -> dict:
    if not name or not STABLE_TAG.fullmatch(tag):
        raise ValueError("Expected a named stable release with a v-prefixed tag.")
    current = api(endpoint)
    if fingerprint(current) != fingerprint(snapshot):
        raise ValueError("Releases changed during generation; rerun Release Drafter.")
    drafts = [release for release in current
              if release["draft"] and not release["prerelease"]
              and release["tag_name"].startswith("v")]
    if len(drafts) > 1:
        raise ValueError("Multiple stable release drafts found; select one before rerunning.")
    payload = {
        "body": body, "name": name, "tag_name": tag, "target_commitish": commit,
        "draft": True, "prerelease": False,
    }
    target = f"{endpoint}/{drafts[0]['id']}" if drafts else endpoint
    result = api(target, "PATCH" if drafts else "POST", payload)
    if any(result[key] != value for key, value in payload.items()) or result["published_at"]:
        raise ValueError("GitHub did not return the expected draft body, tag, and draft status.")
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("snapshot", "update"))
    parser.add_argument("--snapshot", type=Path, required=True)
    args = parser.parse_args()
    endpoint = f"repos/{os.environ['GITHUB_REPOSITORY']}/releases"
    if args.command == "snapshot":
        args.snapshot.write_text(json.dumps(api(endpoint)), encoding="utf-8")
        return
    repo = Path(git(Path.cwd(), "rev-parse", "--show-toplevel").strip())
    commit = git(repo, "rev-parse", "HEAD").strip()
    preview = os.environ["RELEASE_PREVIEW"]
    tag = previous_tag(preview)
    notes = render(repo, f"refs/tags/{tag}" if tag else None, commit)
    body = combine_notes(preview, notes)
    result = update_draft(
        endpoint, json.loads(args.snapshot.read_text(encoding="utf-8")), body,
        os.environ["RELEASE_NAME"], os.environ["RELEASE_TAG"], commit,
    )
    print(f"Updated draft release {result['id']} ({result['tag_name']}).")


if __name__ == "__main__":
    main()
