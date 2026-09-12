import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


FRAGMENTS = ".changes"
FILENAME = re.compile(r"\+[a-z0-9]+(?:-[a-z0-9]+)*\.breaking\.md")
STABLE_TAG = re.compile(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)")
MIGRATION_START = "<!-- migration-notes:start -->"
MIGRATION_END = "<!-- migration-notes:end -->"


def git(repo: Path, *args: str) -> str:
    return subprocess.check_output(
        ["git", *args], cwd=repo, text=True, encoding="utf-8"
    )


def changed_files(repo: Path, base: str, head: str) -> list[tuple[str, str]]:
    entries = git(
        repo, "diff", "--name-status", "--no-renames", "-z", base, head, "--", FRAGMENTS
    ).split("\0")
    return list(zip(entries[0:-1:2], entries[1:-1:2]))


def validate_fragment(name: str, text: str) -> None:
    if not FILENAME.fullmatch(Path(name).name) or Path(name).parent.as_posix() != FRAGMENTS:
        raise ValueError(
            f"Invalid migration fragment filename: {name}. "
            f"Use {FRAGMENTS}/+short-description.breaking.md."
        )
    if MIGRATION_START in text or MIGRATION_END in text:
        raise ValueError(f"{name}: migration fragment contains a reserved release-note marker.")
    if not re.match(r"### [^\n]+\n", text):
        raise ValueError(f"{name}: migration fragment must start with a level-three title.")
    for heading in ("What changed", "How to migrate"):
        section = re.search(
            rf"^#### {heading}\s*\n(.*?)(?=^#|\Z)", text, re.MULTILINE | re.DOTALL
        )
        content = re.sub(r"<!--.*?-->", "", section[1], flags=re.DOTALL).strip() if section else ""
        if not content or content.upper().rstrip(".") in ("TODO", "TBD", "N/A"):
            raise ValueError(f"{name}: migration fragment needs a completed '#### {heading}' section.")


def check_pr(repo: Path, base: str, head: str, labels: list[str]) -> None:
    if "semver:major" in labels and "skip-changelog" in labels:
        raise ValueError("Breaking-change PRs must not use skip-changelog.")
    branch_base = git(repo, "merge-base", base, head).strip()
    changes = changed_files(repo, branch_base, head)
    for status, name in changes:
        if status == "D":
            raise ValueError(f"Keep migration fragments in Git; do not delete or rename {name}.")
        validate_fragment(name, git(repo, "show", f"{head}:{name}"))
    if "semver:major" in labels and not any(status == "A" for status, _ in changes):
        raise ValueError(
            f"semver:major PRs must add a new migration fragment in {FRAGMENTS}; "
            "editing an existing note does not document a new breaking change."
        )


def render(repo: Path, base: str | None, head: str) -> str:
    if base:
        base_commit = git(repo, "rev-parse", "--verify", f"{base}^{{commit}}").strip()
        ancestor = subprocess.run(
            ["git", "merge-base", "--is-ancestor", base_commit, head], cwd=repo
        )
        if ancestor.returncode == 1:
            raise ValueError("The previous release must be an ancestor of the draft commit.")
        ancestor.check_returncode()
        names = [name for status, name in changed_files(repo, base, head) if status == "A"]
    else:
        names = git(repo, "ls-tree", "-rz", "--name-only", head, "--", FRAGMENTS).split("\0")
        names = [name for name in names if name]
    if not names:
        return ""

    with tempfile.TemporaryDirectory() as directory:
        staging = Path(directory)
        for config in ("towncrier.toml", ".github/migration-notes.md.jinja"):
            target = staging / config
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(repo / config, target)
        for name in sorted(names):
            text = git(repo, "show", f"{head}:{name}")
            validate_fragment(name, text)
            target = staging / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text, encoding="utf-8")
        output = subprocess.check_output(
            [sys.executable, "-X", "utf8", "-m", "towncrier", "build", "--draft",
             "--version", "Unreleased", "--dir", str(staging)],
            text=True, encoding="utf-8",
        )
    if "## Breaking changes and migration" not in output or not output.strip():
        raise ValueError("Towncrier did not render the migration notes.")
    return output.strip() + "\n\n"


def previous_tag(preview: str) -> str | None:
    marker = re.match(r"\A<!-- migration-base: ([^\r\n]*?) -->", preview)
    if not marker:
        raise ValueError("Release Drafter preview is missing its migration-base marker.")
    return marker[1] or None


def main() -> None:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    check = commands.add_parser("check")
    check.add_argument("--event", type=Path, required=True)
    build = commands.add_parser("render")
    build.add_argument("--base", help="Previous published release tag; omit for Drafter preview.")
    build.add_argument("--head", default="HEAD")
    args = parser.parse_args()
    repo = Path(git(Path.cwd(), "rev-parse", "--show-toplevel").strip())
    if args.command == "check":
        event = json.loads(args.event.read_text(encoding="utf-8"))
        pr = event["pull_request"]
        check_pr(repo, pr["base"]["sha"], pr["head"]["sha"],
                 [label["name"] for label in pr["labels"]])
        print("Migration note policy passed.")
    else:
        tag = args.base if args.base is not None else previous_tag(os.environ["RELEASE_PREVIEW"])
        base = f"refs/tags/{tag}" if tag else None
        notes = render(repo, base, args.head)
        print(notes, end="")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, subprocess.CalledProcessError) as error:
        print(f"Migration notes failed: {error}", file=sys.stderr)
        sys.exit(1)
