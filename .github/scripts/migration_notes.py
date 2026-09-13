import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from collections.abc import Iterator
from pathlib import Path


FRAGMENTS = ".changes"
GUIDE_STATE = ".github/migration-guides.json"
FILENAME = re.compile(r"\+[a-z0-9]+(?:-[a-z0-9]+)*\.breaking\.md")
STABLE_TAG = re.compile(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)")
MIGRATION_START = "<!-- migration-notes:start -->"
MIGRATION_END = "<!-- migration-notes:end -->"
TOPIC_MARKER_PREFIX = "<!-- migration-topic:"
REQUIRED_SECTIONS = (
    "Previous behavior",
    "New behavior",
    "Type of breaking change",
    "Reason for change",
    "Recommended action",
    "Affected APIs",
)


def git(repo: Path, *args: str) -> str:
    return subprocess.check_output(
        ["git", *args], cwd=repo, text=True, encoding="utf-8"
    )


def changed_files(repo: Path, base: str, head: str, directory: str = FRAGMENTS) -> list[tuple[str, str]]:
    entries = git(
        repo, "diff", "--name-status", "--no-renames", "-z", base, head, "--", directory
    ).split("\0")
    return list(zip(entries[0:-1:2], entries[1:-1:2]))


def markdown_lines(text: str) -> Iterator[tuple[str, re.Match[str] | None]]:
    fence = ""
    for raw_line in text.splitlines(keepends=True):
        line = raw_line.rstrip("\r\n")
        if fence:
            if re.fullmatch(rf" {{0,3}}{re.escape(fence[0])}{{{len(fence)},}}[ \t]*", line):
                fence = ""
            yield raw_line, None
            continue
        opening = re.match(r" {0,3}(`{3,}|~{3,})(.*)$", line)
        if opening and (opening[1][0] == "~" or "`" not in opening[2]):
            fence = opening[1]
            yield raw_line, None
            continue
        yield raw_line, re.match(r" {0,3}(#{1,6})(?:[ \t]+(.*)|$)", line)


def shift_heading_levels(text: str, offset: int) -> str:
    lines = []
    for line, heading in markdown_lines(text):
        if heading:
            level = len(heading[1]) + offset
            if not 1 <= level <= 6:
                raise ValueError(f"Cannot shift Markdown heading {heading[0]!r} to level {level}.")
            line = line[:heading.start(1)] + "#" * level + line[heading.end(1):]
        lines.append(line)
    return "".join(lines)


def find_section(text: str, title: str | None, level: int = 4) -> tuple[int, str]:
    content = []
    position = -1
    active = False
    for line_number, (raw_line, heading) in enumerate(markdown_lines(text)):
        if heading:
            if len(heading[1]) > level:
                continue
            if active:
                break
            heading_title = re.sub(r"[ \t]+#+[ \t]*$", "", heading[2] or "").strip()
            if len(heading[1]) == level and (title is None or heading_title == title):
                position = line_number
                active = True
                continue
        if active:
            content.append(raw_line)
    return position, "".join(content)


def validate_fragment(name: str, text: str) -> None:
    if not FILENAME.fullmatch(Path(name).name) or Path(name).parent.as_posix() != FRAGMENTS:
        raise ValueError(
            f"Invalid migration fragment filename: {name}. "
            f"Use {FRAGMENTS}/+short-description.breaking.md."
        )
    if Path(name).name == "+readme.breaking.md":
        raise ValueError(f"{name}: migration fragment filename 'readme' is reserved for the version index.")
    if MIGRATION_START in text or MIGRATION_END in text or TOPIC_MARKER_PREFIX in text:
        raise ValueError(f"{name}: migration fragment contains a reserved release-note marker.")
    if not re.match(r"### [^\n]+\n", text):
        raise ValueError(f"{name}: migration fragment must start with a level-three title.")
    positions = []
    for heading in REQUIRED_SECTIONS:
        position, section = find_section(text, heading)
        content = re.sub(r"<!--.*?-->", "", section, flags=re.DOTALL).strip()
        if not content or content.upper().rstrip(".") in ("TODO", "TBD", "N/A"):
            raise ValueError(f"{name}: migration fragment needs a completed '#### {heading}' section.")
        positions.append(position)
    if positions != sorted(positions):
        raise ValueError(
            f"{name}: required sections must appear in this order: "
            + ", ".join(REQUIRED_SECTIONS) + "."
        )


def validate_guide(name: str, text: str) -> None:
    if name == "docs/migrations/README.md":
        return
    path = re.fullmatch(r"docs/migrations/([^/]+)/([^/]+)\.md", name)
    if not path or not STABLE_TAG.fullmatch(f"v{path[1]}"):
        raise ValueError(f"Invalid migration guide path: {name}.")
    if path[2] == "README":
        return
    if f"**Version introduced:** {path[1]}\n" not in text:
        raise ValueError(f"{name}: Version introduced must match its version directory.")
    position, _ = find_section(text, "Breaking changes and migration", level=2)
    if position != -1:
        topic = "".join(text.splitlines(keepends=True)[position + 1:]).lstrip("\r\n")
    else:
        position, _ = find_section(text, None, level=1)
        if position == -1:
            raise ValueError(f"{name}: migration topic is missing its title heading.")
        topic = "".join(text.splitlines(keepends=True)[position:])
        topic = shift_heading_levels(topic, 2).lstrip(" ")
    validate_fragment(f"{FRAGMENTS}/+{path[2]}.breaking.md", topic)


def pending_guide_versions(text: str | None) -> list[str]:
    state = json.loads(text) if text is not None else {"pending_versions": []}
    if not isinstance(state, dict) or set(state) != {"pending_versions"}:
        raise ValueError("Invalid migration guide state.")
    pending = state["pending_versions"]
    if not isinstance(pending, list) or any(
        not isinstance(version, str) or not STABLE_TAG.fullmatch(f"v{version}") for version in pending
    ) or len(pending) != len(set(pending)):
        raise ValueError("Invalid pending migration guide versions.")
    return pending


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
    guide_changes = changed_files(repo, branch_base, head, "docs/migrations")
    pending = []
    if any(status == "D" and name.endswith(".md") for status, name in guide_changes):
        state_files = git(
            repo, "ls-tree", "-rz", "--name-only", base, "--", GUIDE_STATE
        ).split("\0")
        pending = pending_guide_versions(
            git(repo, "show", f"{base}:{GUIDE_STATE}") if GUIDE_STATE in state_files else None
        )
    for status, name in guide_changes:
        if not name.endswith(".md"):
            continue
        if status == "D":
            parts = name.split("/")
            if len(parts) != 4 or parts[2] not in pending:
                raise ValueError(
                    f"Cannot delete or rename {name}: "
                    "its version must be pending at the PR base."
                )
        else:
            validate_guide(name, git(repo, "show", f"{head}:{name}"))


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
            target.write_text(git(repo, "show", f"{head}:{config}"), encoding="utf-8")
        for name in sorted(names):
            text = git(repo, "show", f"{head}:{name}")
            validate_fragment(name, text)
            slug = Path(name).name.removeprefix("+").removesuffix(".breaking.md")
            text = f"{TOPIC_MARKER_PREFIX} {slug} -->\n{text}"
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
