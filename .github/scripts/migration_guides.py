import os
from pathlib import Path

from migration_notes import MIGRATION_END, MIGRATION_START, STABLE_TAG
from update_release_draft import api


def migration_section(body: str) -> str | None:
    body = body.replace("\r\n", "\n")
    if MIGRATION_START not in body and MIGRATION_END not in body:
        return None
    if body.count(MIGRATION_START) != 1 or body.count(MIGRATION_END) != 1:
        raise ValueError("Published migration notes must contain exactly one start/end marker pair.")
    start = body.index(MIGRATION_START) + len(MIGRATION_START)
    end = body.index(MIGRATION_END)
    section = body[start:end].strip()
    if end < start or not section.startswith("## Breaking changes and migration\n"):
        raise ValueError("Published migration notes have reversed markers or an invalid section.")
    if not section.removeprefix("## Breaking changes and migration\n").strip():
        raise ValueError("Published migration notes are empty.")
    return section


def guide_documents(releases: list[dict], repository: str) -> dict[str, str]:
    guides = {}
    for release in releases:
        if release["draft"] or release["prerelease"]:
            continue
        section = migration_section(release["body"] or "")
        if section is None:
            continue
        tag = release["tag_name"]
        if not STABLE_TAG.fullmatch(tag):
            raise ValueError(f"Cannot generate a stable migration guide for release tag {tag!r}.")
        version = tag[1:]
        if version in guides:
            raise ValueError(f"Multiple published releases contain migration notes for {version}.")
        guides[version] = (
            f"# Upgrade to {version}\n\n"
            f"Migration guidance from the [{version} release notes]"
            f"(https://github.com/{repository}/releases/tag/{tag}).\n\n"
            f"{section}\n"
        )
    return guides


def index_document(versions: set[str]) -> str:
    entries = "\n".join(
        f"- [Upgrade to {version}]({version}.md)"
        for version in sorted(versions, key=lambda value: tuple(map(int, value.split("."))), reverse=True)
    ) or "No versioned migration guides have been published yet."
    return (
        "# Migration guides\n\n"
        "Guides are grouped by the release that introduced each breaking change.\n"
        "When upgrading across several releases, read each applicable guide.\n"
        "Releases without migration instructions are not listed.\n\n"
        "## Published releases\n\n"
        f"{entries}\n"
    )


def write_guides(repo: Path, releases: list[dict], repository: str) -> None:
    guides = guide_documents(releases, repository)
    directory = repo / "docs" / "migrations"
    directory.mkdir(parents=True, exist_ok=True)
    versions = {path.stem for path in directory.glob("*.md")
                if STABLE_TAG.fullmatch(f"v{path.stem}")}
    for version, content in guides.items():
        (directory / f"{version}.md").write_text(content, encoding="utf-8", newline="\n")
    (directory / "README.md").write_text(
        index_document(versions | guides.keys()), encoding="utf-8", newline="\n"
    )
    print(f"Prepared {len(guides)} versioned migration guides from published release notes.")


if __name__ == "__main__":
    repository = os.environ["GITHUB_REPOSITORY"]
    write_guides(Path.cwd(), api(f"repos/{repository}/releases"), repository)
