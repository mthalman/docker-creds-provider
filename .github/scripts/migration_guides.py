import json
import re
from pathlib import Path

from migration_notes import (
    FRAGMENTS, GUIDE_STATE as STATE, STABLE_TAG, TOPIC_MARKER_PREFIX, git,
    pending_guide_versions, validate_fragment, validate_guide,
)


GUIDES = "docs/migrations"


def migration_topics(section: str) -> dict[str, tuple[str, str]]:
    section = section.replace("\r\n", "\n")
    markers = list(re.finditer(
        r"^<!-- migration-topic: ([a-z0-9]+(?:-[a-z0-9]+)*) -->$", section, re.MULTILINE
    ))
    if not markers or section.count(TOPIC_MARKER_PREFIX) != len(markers):
        raise ValueError("Migration topics have missing or malformed topic markers.")
    if section[:markers[0].start()].strip() != "## Breaking changes and migration":
        raise ValueError("Migration notes contain text outside the marked topics.")
    topics = {}
    for index, marker in enumerate(markers):
        slug = marker[1]
        if slug == "readme" or slug in topics:
            raise ValueError(f"Migration topic slug {slug!r} is reserved or duplicated.")
        end = markers[index + 1].start() if index + 1 < len(markers) else len(section)
        text = section[marker.end():end].strip()
        heading = re.match(r"### ([^\n]+)\n", text)
        if not heading:
            raise ValueError(f"Migration topic {slug!r} needs a level-three title.")
        validate_fragment(f"{FRAGMENTS}/+{slug}.breaking.md", text)
        topics[slug] = (heading[1], text)
    return topics


def link_label(title: str) -> str:
    return title.replace("\\", "\\\\").replace("[", "\\[").replace("]", "\\]")


def guide_documents(notes: str, tag: str) -> dict[str, str]:
    if not STABLE_TAG.fullmatch(tag):
        raise ValueError(f"Cannot generate migration guides for release tag {tag!r}.")
    if not notes:
        return {}
    version = tag[1:]
    topics = migration_topics(notes)
    documents = {}
    links = []
    for slug, (title, text) in topics.items():
        documents[f"{slug}.md"] = (
            f"# Upgrade to {version}\n\n"
            f"**Version introduced:** {version}\n\n"
            f"## Breaking changes and migration\n\n{text}\n"
        )
        links.append(f"- [{link_label(title)}]({slug}.md)")
    documents["README.md"] = (
        f"# Upgrade to {version}\n\n"
        "Read each migration topic that applies to your application.\n\n"
        "## Migration topics\n\n" + "\n".join(links) + "\n"
    )
    return documents


def linked_notes(notes: str, tag: str, repository: str) -> str:
    if not STABLE_TAG.fullmatch(tag):
        raise ValueError(f"Cannot link migration guides for release tag {tag!r}.")
    if not notes:
        return ""
    links = [
        f"- [{link_label(title)}]"
        f"(https://github.com/{repository}/blob/main/{GUIDES}/{tag[1:]}/{slug}.md)"
        for slug, (title, _) in migration_topics(notes).items()
    ]
    return "## Breaking changes and migration\n\n" + "\n".join(links) + "\n"


def index_document(versions: set[str]) -> str:
    entries = "\n".join(
        f"- [Upgrade to {version}]({version}/README.md)"
        for version in sorted(versions, key=lambda value: tuple(map(int, value.split("."))), reverse=True)
    ) or "No versioned migration guides are available yet."
    return (
        "# Migration guides\n\n"
        "Migration topics are grouped by the release that introduced each breaking change.\n"
        "When upgrading across several releases, read the applicable topics in each version.\n"
        "Releases without migration instructions are not listed. Guides for upcoming releases\n"
        "may appear here before publication.\n\n"
        "## Releases\n\n"
        f"{entries}\n"
    )


def committed_guides(repo: Path, commit: str) -> dict[str, str]:
    names = git(repo, "ls-tree", "-rz", "--name-only", commit, "--", GUIDES, STATE).split("\0")
    return {
        name: git(repo, "show", f"{commit}:{name}")
        for name in names if name and (name.endswith(".md") or name == STATE)
    }


def plan_guides(repo: Path, commit: str, notes: str, tag: str, releases: list[dict]) -> dict[str, str]:
    generated = guide_documents(notes, tag)
    documents = committed_guides(repo, commit)
    for name, text in documents.items():
        if name != STATE:
            validate_guide(name, text)
    pending = pending_guide_versions(documents.get(STATE))
    published = {
        release["tag_name"][1:] for release in releases
        if not release["draft"] and not release["prerelease"]
        and STABLE_TAG.fullmatch(release["tag_name"])
    }
    version = tag[1:]
    if version in published:
        raise ValueError("Release Drafter selected an already published version.")
    retained = []
    for old_version in pending:
        if old_version in published:
            continue
        prefix = f"{GUIDES}/{old_version}/"
        referenced = any(
            f"/blob/main/{prefix}" in (release.get("body") or "") for release in releases
        )
        if referenced and (old_version != version or not generated):
            retained.append(old_version)
            continue
        documents = {name: text for name, text in documents.items() if not name.startswith(prefix)}
    prefix = f"{GUIDES}/{version}/"
    if generated and any(name.startswith(prefix) for name in documents):
        raise ValueError(f"Refusing to overwrite untracked migration guide version {version}.")
    for name, text in generated.items():
        documents[prefix + name] = text
    if generated or STATE in documents:
        documents[STATE] = json.dumps(
            {"pending_versions": sorted(retained + ([version] if generated else []))}, indent=2
        ) + "\n"
    versions = {name.split("/")[2] for name in documents
                if name.startswith(f"{GUIDES}/") and name.count("/") == 3
                and name.endswith("/README.md")}
    documents[f"{GUIDES}/README.md"] = index_document(versions)
    return documents


def guides_ready(repo: Path, commit: str, documents: dict[str, str]) -> bool:
    return committed_guides(repo, commit) == documents


def write_guides(repo: Path, commit: str, documents: dict[str, str]) -> None:
    for name in committed_guides(repo, commit).keys() - documents.keys():
        (repo / name).unlink()
    for name, content in documents.items():
        target = repo / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8", newline="\n")
