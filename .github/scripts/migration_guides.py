import os
import re
from pathlib import Path

from migration_notes import MIGRATION_END, MIGRATION_START, STABLE_TAG, TOPIC_MARKER_PREFIX
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


def migration_topics(section: str) -> dict[str, tuple[str, str]]:
    markers = list(re.finditer(
        r"^<!-- migration-topic: ([a-z0-9]+(?:-[a-z0-9]+)*) -->$", section, re.MULTILINE
    ))
    if not markers or section.count(TOPIC_MARKER_PREFIX) != len(markers):
        raise ValueError("Published migration topics have missing or malformed topic markers.")
    if section[:markers[0].start()].strip() != "## Breaking changes and migration":
        raise ValueError("Published migration notes contain text outside the marked topics.")
    topics = {}
    for index, marker in enumerate(markers):
        slug = marker[1]
        if slug == "readme" or slug in topics:
            raise ValueError(f"Published migration topic slug {slug!r} is reserved or duplicated.")
        end = markers[index + 1].start() if index + 1 < len(markers) else len(section)
        text = section[marker.end():end].strip()
        heading = re.match(r"### ([^\n]+)\n", text)
        if not heading:
            raise ValueError(f"Published migration topic {slug!r} needs a level-three title.")
        topics[slug] = (heading[1], text)
    return topics


def guide_documents(releases: list[dict], repository: str) -> dict[str, dict[str, str]]:
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
        topics = migration_topics(section)
        provenance = (
            f"Migration guidance from the [{version} release notes]"
            f"(https://github.com/{repository}/releases/tag/{tag}).\n\n"
        )
        documents = {}
        links = []
        for slug, (title, text) in topics.items():
            documents[f"{slug}.md"] = (
                f"# Upgrade to {version}\n\n{provenance}"
                f"## Breaking changes and migration\n\n{text}\n"
            )
            label = title.replace("\\", "\\\\").replace("[", "\\[").replace("]", "\\]")
            links.append(f"- [{label}]({slug}.md)")
        documents["README.md"] = (
            f"# Upgrade to {version}\n\n{provenance}"
            "Read each migration topic that applies to your application.\n\n"
            "## Migration topics\n\n" + "\n".join(links) + "\n"
        )
        guides[version] = documents
    return guides


def index_document(versions: set[str]) -> str:
    entries = "\n".join(
        f"- [Upgrade to {version}]({version}/README.md)"
        for version in sorted(versions, key=lambda value: tuple(map(int, value.split("."))), reverse=True)
    ) or "No versioned migration guides have been published yet."
    return (
        "# Migration guides\n\n"
        "Migration topics are grouped by the release that introduced each breaking change.\n"
        "When upgrading across several releases, read the applicable topics in each version.\n"
        "Releases without migration instructions are not listed.\n\n"
        "## Published releases\n\n"
        f"{entries}\n"
    )


def write_guides(repo: Path, releases: list[dict], repository: str) -> None:
    guides = guide_documents(releases, repository)
    directory = repo / "docs" / "migrations"
    directory.mkdir(parents=True, exist_ok=True)
    versions = {path.name for path in directory.iterdir()
                if path.is_dir() and STABLE_TAG.fullmatch(f"v{path.name}")
                and (path / "README.md").is_file()}
    for version, documents in guides.items():
        version_directory = directory / version
        version_directory.mkdir(exist_ok=True)
        for name, content in documents.items():
            (version_directory / name).write_text(content, encoding="utf-8", newline="\n")
        for obsolete in version_directory.glob("*.md"):
            if obsolete.name not in documents:
                obsolete.unlink()
    (directory / "README.md").write_text(
        index_document(versions | guides.keys()), encoding="utf-8", newline="\n"
    )
    print(f"Prepared {len(guides)} versioned migration guides from published release notes.")


if __name__ == "__main__":
    repository = os.environ["GITHUB_REPOSITORY"]
    write_guides(Path.cwd(), api(f"repos/{repository}/releases"), repository)
