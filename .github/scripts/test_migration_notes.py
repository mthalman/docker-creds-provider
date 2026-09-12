import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from migration_notes import MIGRATION_END, MIGRATION_START, TOPIC_MARKER_PREFIX, check_pr, main, previous_tag, render, validate_fragment
from migration_guides import guide_documents, index_document, migration_section, migration_topics, write_guides
from update_release_draft import combine_notes, update_draft


ROOT = Path(__file__).resolve().parents[2]
REQUIRED_HEADINGS = (
    "Previous behavior", "New behavior", "Type of breaking change",
    "Reason for change", "Recommended action", "Affected APIs",
)
NOTE = """### Credential helper errors

#### Previous behavior

Malformed helper JSON threw `JsonException`.

#### New behavior

Malformed helper JSON now throws `InvalidOperationException`.

#### Type of breaking change

Behavioral change.

#### Reason for change

Avoid exposing credential-helper output in diagnostics.

#### Recommended action

Catch the new exception type and inspect its inner exception.

#### Affected APIs

`CredsProvider.GetCredentialsAsync` (all overloads).
"""


class MigrationFormatTests(unittest.TestCase):
    def test_dotnet_format_is_valid(self):
        validate_fragment(".changes/+helper-errors.breaking.md", NOTE)

    def test_each_required_section_must_be_present(self):
        for heading in REQUIRED_HEADINGS:
            with self.subTest(heading=heading):
                text = NOTE.replace(f"#### {heading}", f"#### Omitted {heading}")
                with self.assertRaisesRegex(ValueError, heading):
                    validate_fragment(".changes/+helper-errors.breaking.md", text)

    def test_each_required_section_must_have_completed_content(self):
        for heading in REQUIRED_HEADINGS:
            before, separator, after = NOTE.partition(f"#### {heading}\n")
            _, next_heading, remaining = after.partition("\n#### ")
            for content in ("", "TODO", "TBD.", "N/A", "<!-- Fill this in. -->"):
                with self.subTest(heading=heading, content=content):
                    text = before + separator + f"\n{content}\n" + next_heading + remaining
                    with self.assertRaisesRegex(ValueError, heading):
                        validate_fragment(".changes/+helper-errors.breaking.md", text)

    def test_legacy_two_section_format_is_rejected(self):
        text = "### Topic\n\n#### What changed\n\nChanged behavior.\n\n#### How to migrate\n\nUpdate code.\n"
        with self.assertRaisesRegex(ValueError, "Previous behavior"):
            validate_fragment(".changes/+helper-errors.breaking.md", text)

    def test_repository_fragments_follow_format(self):
        for path in (ROOT / ".changes").glob("*.md"):
            with self.subTest(path=path.name):
                validate_fragment(path.relative_to(ROOT).as_posix(), path.read_text(encoding="utf-8"))

    def test_contributor_template_follows_format(self):
        document = (ROOT / "CONTRIBUTING.md").read_text(encoding="utf-8")
        template = document.split("```markdown\n", 1)[1].split("\n```", 1)[0]
        validate_fragment(".changes/+example.breaking.md", template + "\n")


class FragmentSectionTests(unittest.TestCase):
    def validate(self, text):
        validate_fragment(".changes/+helper-errors.breaking.md", text)

    def test_nested_before_and_after_headings_are_valid(self):
        text = NOTE.replace(
            "Malformed helper JSON now throws `InvalidOperationException`.",
            "##### Previous behavior\n\nMalformed JSON threw JsonException.\n\n"
            "##### New behavior\n\nMalformed JSON throws InvalidOperationException.",
        ).replace(
            "Catch the new exception type and inspect its inner exception.",
            "##### Before\n\nCatch JsonException.\n\n"
            "##### After\n\nCatch InvalidOperationException and inspect InnerException.",
        )
        self.validate(text)

    def test_required_heading_inside_fence_does_not_satisfy_requirement(self):
        for fence in ("```", "~~~~"):
            with self.subTest(fence=fence):
                text = NOTE.split("#### Recommended action")[0] + (
                    f"{fence}markdown\n#### Recommended action\nExample text, not guidance.\n{fence}\n"
                )
                with self.assertRaisesRegex(ValueError, "Recommended action"):
                    self.validate(text)

    def test_shorter_or_different_fence_does_not_close_code_block(self):
        for false_close in ("```", "~~~~", "```` followed by text"):
            with self.subTest(false_close=false_close):
                text = NOTE.split("#### Recommended action")[0] + (
                    f"````markdown\n{false_close}\n"
                    "#### Recommended action\nStill inside a code example.\n````\n"
                )
                with self.assertRaisesRegex(ValueError, "Recommended action"):
                    self.validate(text)

    def test_section_after_closed_indented_fence_is_recognized(self):
        for fence in ("```", "~~~"):
            with self.subTest(fence=fence):
                text = NOTE.replace(
                    "#### Recommended action",
                    f"   {fence}markdown\n#### Not a real section\n   {fence}{fence}\n\n"
                    "#### Recommended action",
                )
                self.validate(text)

    def test_empty_section_does_not_consume_following_peer_or_parent(self):
        for heading in ("#### Details", "### Another topic", "## Appendix"):
            with self.subTest(heading=heading):
                text = NOTE.split("Catch the new")[0] + f"{heading}\n\nUnrelated content.\n"
                with self.assertRaisesRegex(ValueError, "Recommended action"):
                    self.validate(text)


class MigrationNotesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Migration tests")
        self.git("config", "user.email", "migration-tests@example.invalid")
        self.git("config", "core.autocrlf", "false")
        self.write("initial.txt", "Initial commit\n")
        self.base = self.commit()

    def git(self, *args):
        return subprocess.check_output(
            ["git", *args], cwd=self.repo, text=True, encoding="utf-8"
        ).strip()

    def write(self, name, text):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def note(self, slug="helper", text=NOTE):
        self.write(f".changes/+{slug}.breaking.md", text)

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-qm", "Test changes")
        return self.git("rev-parse", "HEAD")

    def configure_renderer(self):
        for name in ("towncrier.toml", ".github/migration-notes.md.jinja"):
            target = self.repo / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)

    def test_major_pr_requires_new_note(self):
        with self.assertRaisesRegex(ValueError, "new migration fragment"):
            check_pr(self.repo, self.base, self.base, ["semver:major"])

    def test_major_pr_accepts_valid_new_note(self):
        self.note()
        check_pr(self.repo, self.base, self.commit(), ["semver:major"])

    def test_nonmajor_pr_does_not_require_note(self):
        check_pr(self.repo, self.base, self.base, ["semver:patch"])

    def test_major_pr_cannot_skip_changelog(self):
        self.note()
        with self.assertRaisesRegex(ValueError, "skip-changelog"):
            check_pr(self.repo, self.base, self.commit(), ["semver:major", "skip-changelog"])

    def test_existing_note_edit_does_not_satisfy_major_requirement(self):
        self.note()
        base = self.commit()
        self.note(text=NOTE + "\nMore detail.\n")
        with self.assertRaisesRegex(ValueError, "new migration fragment"):
            check_pr(self.repo, base, self.commit(), ["semver:major"])

    def test_notes_require_meaningful_sections_even_for_nonmajor_pr(self):
        for text in ("", NOTE.replace("#### Recommended action", "#### Details"),
                     NOTE.split("Catch the new")[0] + "TODO\n"):
            with self.subTest(text=text):
                self.note(text=text)
                with self.assertRaisesRegex(ValueError, "migration fragment"):
                    check_pr(self.repo, self.base, self.commit(), ["semver:patch"])

    def test_invalid_fragment_filename_fails(self):
        self.write(".changes/wrong.md", NOTE)
        with self.assertRaisesRegex(ValueError, "filename"):
            check_pr(self.repo, self.base, self.commit(), ["semver:major"])

    def test_deleting_or_renaming_note_fails(self):
        self.note()
        base = self.commit()
        self.git("mv", ".changes/+helper.breaking.md", ".changes/+renamed.breaking.md")
        with self.assertRaisesRegex(ValueError, "delete or rename"):
            check_pr(self.repo, base, self.commit(), ["semver:patch"])

    def test_other_branch_note_does_not_satisfy_major_requirement(self):
        self.git("checkout", "-qb", "other")
        self.note()
        other = self.commit()
        self.git("checkout", "-q", self.base)
        self.write("feature.txt", "No migration note\n")
        head = self.commit()
        with self.assertRaisesRegex(ValueError, "new migration fragment"):
            check_pr(self.repo, other, head, ["semver:major"])

    def test_policy_runs_base_validator_not_pr_code(self):
        validator_path = ".github/scripts/migration_notes.py"
        trusted_validator = (ROOT / validator_path).read_text(encoding="utf-8")
        self.write(validator_path, trusted_validator)
        base = self.commit()
        sentinel = "UNTRUSTED PR CODE EXECUTED"
        for add_note in (False, True):
            with self.subTest(add_note=add_note):
                self.git("checkout", "-q", base)
                self.write(validator_path, f"print({sentinel!r})\n")
                self.write(".github/scripts/requirements.txt", "invalid requirement ???\n")
                self.write("sitecustomize.py", f"print({sentinel!r})\n")
                if add_note:
                    self.note()
                head = self.commit()
                self.git("checkout", "-q", base)
                with tempfile.TemporaryDirectory() as directory:
                    event_path = Path(directory) / "event.json"
                    event_path.write_text(json.dumps({
                        "pull_request": {
                            "base": {"sha": base},
                            "head": {"sha": head},
                            "labels": [{"name": "semver:major"}],
                        },
                    }), encoding="utf-8")
                    result = subprocess.run(
                        [sys.executable, "-I", validator_path, "check", "--event", str(event_path)],
                        cwd=self.repo, capture_output=True, text=True, encoding="utf-8",
                    )
                self.assertEqual(result.returncode, 0 if add_note else 1, result.stderr)
                self.assertNotIn(sentinel, result.stdout + result.stderr)
                if add_note:
                    self.assertIn("Migration note policy passed.", result.stdout)
                else:
                    self.assertIn("must add a new migration fragment", result.stderr)
                self.assertEqual(self.git("rev-parse", "HEAD"), base)
                self.assertEqual(
                    (self.repo / validator_path).read_text(encoding="utf-8"), trusted_validator
                )

    def test_preview_uses_exact_drafter_boundary(self):
        self.assertEqual(
            previous_tag("<!-- migration-base: v2.3.0 -->\n\n## Changes"), "v2.3.0"
        )
        self.assertIsNone(previous_tag("<!-- migration-base:  -->\n\n## Changes"))
        self.assertEqual(
            previous_tag("<!-- migration-base: v2.3.0 -->## Changes"), "v2.3.0"
        )
        with self.assertRaisesRegex(ValueError, "preview"):
            previous_tag("## Changes")

    def test_render_empty_range_has_no_migration_heading(self):
        self.configure_renderer()
        self.assertEqual(render(self.repo, self.base, self.base), "")

    def test_render_excludes_released_notes_and_preserves_markdown(self):
        self.configure_renderer()
        self.note("released", NOTE.replace("Credential helper errors", "Old change"))
        base = self.commit()
        self.note("new", NOTE + "\n```csharp\ncatch (InvalidOperationException ex)\n```\n")
        head = self.commit()
        before = self.git("status", "--porcelain", "--untracked-files=all")
        output = render(self.repo, base, head)
        self.assertIn("## Breaking changes and migration", output)
        self.assertIn(NOTE.strip(), output)
        self.assertIn("```csharp\ncatch (InvalidOperationException ex)\n```", output)
        self.assertNotIn("Old change", output)
        self.assertEqual(output, render(self.repo, base, head))
        self.assertEqual(before, self.git("status", "--porcelain", "--untracked-files=all"))
        self.assertTrue((self.repo / ".changes/+new.breaking.md").exists())
        self.assertEqual(render(self.repo, head, head), "")

    def test_render_first_release_includes_all_notes(self):
        self.configure_renderer()
        self.note()
        self.assertIn(NOTE.strip(), render(self.repo, None, self.commit()))

    def test_multiple_fragments_have_separate_headings(self):
        self.configure_renderer()
        self.note("first")
        self.note("second", NOTE.replace("Credential helper errors", "Another change"))
        head = self.commit()
        output = render(self.repo, None, head)
        self.assertTrue(output.startswith("## Breaking changes and migration\n\n<!-- migration-topic: "))
        self.assertIn("(all overloads).\n\n<!-- migration-topic: ", output)
        topics = migration_topics(output.strip())
        self.assertEqual(set(topics), {"first", "second"})
        self.assertEqual(output, render(self.repo, None, head))

    def test_unicode_and_drafter_tokens_survive_full_markdown_pipeline(self):
        self.configure_renderer()
        text = NOTE + '\nOld \u2192 new; caf\u00e9.\n\n```sh\necho "$OWNER/$REPOSITORY/$RESOLVED_VERSION"\n```\n'
        self.note(text=text)
        notes = render(self.repo, None, self.commit())
        preview = "<!-- migration-base: v2.3.0 -->## What's Changed\n\n- Existing PR\n"
        combined = combine_notes(preview, notes)
        self.assertIn(text.strip(), combined)
        self.assertTrue(combined.endswith(f"```\n{MIGRATION_END}\n\n## What's Changed\n\n- Existing PR\n"))
        self.assertIn(text.strip(), migration_section(combined))
        self.assertNotIn("migration-base:", combined)

    def test_render_uses_committed_note_not_working_tree(self):
        self.configure_renderer()
        self.note()
        head = self.commit()
        self.note(text="Uncommitted invalid content")
        self.assertIn(NOTE.strip(), render(self.repo, self.base, head))

    def test_cli_renders_exact_preview_boundary(self):
        with patch("migration_notes.os.environ", {
            "RELEASE_PREVIEW": "<!-- migration-base: v2.3.0 -->\n",
        }), patch("sys.argv", ["migration_notes.py", "render"]), \
                patch("migration_notes.git", return_value=str(self.repo)), \
                patch("migration_notes.render", return_value=NOTE + "\n") as renderer, \
                patch("builtins.print") as output:
            main()
        renderer.assert_called_once_with(self.repo, "refs/tags/v2.3.0", "HEAD")
        output.assert_called_once_with(NOTE + "\n", end="")

    def test_render_rejects_invalid_note_before_drafting(self):
        self.configure_renderer()
        self.note(text="TODO")
        with self.assertRaisesRegex(ValueError, "migration fragment"):
            render(self.repo, self.base, self.commit())

    def test_reserved_migration_markers_are_rejected_in_fragments(self):
        for marker in (MIGRATION_START, TOPIC_MARKER_PREFIX):
            with self.subTest(marker=marker):
                self.note(text=NOTE + marker)
                with self.assertRaisesRegex(ValueError, "reserved"):
                    check_pr(self.repo, self.base, self.commit(), ["semver:major"])

    def test_readme_fragment_name_is_reserved(self):
        self.note("readme")
        with self.assertRaisesRegex(ValueError, "reserved"):
            check_pr(self.repo, self.base, self.commit(), ["semver:major"])

    def test_render_does_not_reintroduce_edited_released_notes(self):
        self.configure_renderer()
        self.note()
        base = self.commit()
        self.note(text=NOTE + "\nCorrection.\n")
        self.assertEqual(render(self.repo, base, self.commit()), "")

    def test_render_missing_or_unrelated_base_fails(self):
        with self.assertRaises(subprocess.CalledProcessError):
            render(self.repo, "missing-ref", self.base)
        self.git("checkout", "--orphan", "unrelated")
        self.git("rm", "-rf", ".")
        self.write("unrelated.txt", "Unrelated history\n")
        unrelated = self.commit()
        with self.assertRaisesRegex(ValueError, "ancestor"):
            render(self.repo, self.base, unrelated)


class DraftUpdateTests(unittest.TestCase):
    def setUp(self):
        self.endpoint = "repos/owner/repo/releases"
        self.draft = {
            "id": 10, "draft": True, "prerelease": False, "name": "3.0.0",
            "tag_name": "v3.0.0", "target_commitish": "main",
            "updated_at": "2026-09-01", "published_at": None,
            "body": "Existing notes",
        }
        self.body = "## Breaking changes\n\n## What's Changed\n"

    def result(self):
        return {**self.draft, "body": self.body, "target_commitish": "commit"}

    def update(self, snapshot):
        return update_draft(self.endpoint, snapshot, self.body, "3.0.0", "v3.0.0", "commit")

    def test_updates_same_existing_draft_and_preserves_draft_status(self):
        with patch("update_release_draft.api", side_effect=[[self.draft], self.result()]) as api:
            self.update([self.draft])
        target, method, payload = api.call_args_list[-1].args
        self.assertEqual(target, self.endpoint + "/10")
        self.assertEqual(method, "PATCH")
        self.assertTrue(payload["draft"])
        self.assertFalse(payload["prerelease"])
        self.assertEqual(payload["tag_name"], "v3.0.0")
        self.assertEqual(payload["body"], self.body)

    def test_creates_draft_when_no_existing_draft(self):
        with patch("update_release_draft.api", side_effect=[[], self.result()]) as api:
            self.update([])
        self.assertEqual(api.call_args_list[-1].args[:2], (self.endpoint, "POST"))
        self.assertTrue(api.call_args_list[-1].args[2]["draft"])

    def test_unrelated_drafts_are_not_updated(self):
        for tag in ("v-next", "v3.0.0-preview.1", "v03.0.0", "unrelated"):
            with self.subTest(tag=tag):
                unrelated = {**self.draft, "id": 11, "tag_name": tag}
                with patch("update_release_draft.api",
                           side_effect=[[unrelated], self.result()]) as api:
                    self.update([unrelated])
                self.assertEqual(
                    api.call_args_list[-1].args[:2], (self.endpoint, "POST")
                )

    def test_unrelated_drafts_do_not_hide_stable_draft(self):
        drafts = [self.draft, {**self.draft, "id": 11, "tag_name": "v-next"}]
        with patch("update_release_draft.api",
                   side_effect=[drafts, self.result()]) as api:
            self.update(drafts)
        self.assertEqual(
            api.call_args_list[-1].args[:2], (self.endpoint + "/10", "PATCH")
        )

    def test_publishing_between_preview_and_update_aborts_without_writing(self):
        published = {**self.draft, "draft": False, "published_at": "2026-09-02"}
        with patch("update_release_draft.api", return_value=[published]) as api:
            with self.assertRaisesRegex(ValueError, "Releases changed"):
                self.update([self.draft])
        api.assert_called_once_with(self.endpoint)

    def test_manual_draft_edit_during_generation_aborts_without_writing(self):
        edited = {**self.draft, "updated_at": "2026-09-02"}
        with patch("update_release_draft.api", return_value=[edited]) as api:
            with self.assertRaisesRegex(ValueError, "Releases changed"):
                self.update([self.draft])
        api.assert_called_once_with(self.endpoint)

    def test_multiple_drafts_aborts_without_writing(self):
        drafts = [self.draft, {**self.draft, "id": 11}]
        with patch("update_release_draft.api", return_value=drafts) as api:
            with self.assertRaisesRegex(ValueError, "Multiple"):
                self.update(drafts)
        api.assert_called_once_with(self.endpoint)

    def test_empty_migration_output_does_not_add_section(self):
        self.assertEqual(
            combine_notes("<!-- migration-base: v2.3.0 -->## What's Changed", ""),
            "## What's Changed",
        )


class MigrationGuideTests(unittest.TestCase):
    def release(self, tag="v3.0.0", **overrides):
        return {
            "tag_name": tag, "draft": False, "prerelease": False,
            "body": combine_notes(
                "<!-- migration-base: v2.3.0 -->## What's Changed\n\n- Product fix",
                "## Breaking changes and migration\n\n"
                "<!-- migration-topic: credential-helper-errors -->\n" + NOTE,
            ),
            **overrides,
        }

    def test_guide_contains_only_published_migration_section(self):
        guides = guide_documents([self.release()], "owner/repo")
        self.assertEqual(set(guides), {"3.0.0"})
        topic = guides["3.0.0"]["credential-helper-errors.md"]
        self.assertEqual(set(guides["3.0.0"]), {"credential-helper-errors.md", "README.md"})
        self.assertTrue(topic.startswith("# Upgrade to 3.0.0\n\n"))
        self.assertIn("**Version introduced:** 3.0.0\n", topic)
        self.assertIn("https://github.com/owner/repo/releases/tag/v3.0.0", topic)
        self.assertIn(NOTE.strip(), topic)
        self.assertNotIn("What's Changed", topic)
        self.assertNotIn("migration-notes:", topic)
        self.assertNotIn("migration-topic:", topic)
        self.assertIn("[Credential helper errors](credential-helper-errors.md)", guides["3.0.0"]["README.md"])

    def test_published_topics_require_the_same_sections_as_fragments(self):
        for heading in REQUIRED_HEADINGS:
            with self.subTest(heading=heading):
                release = self.release()
                release["body"] = release["body"].replace(f"#### {heading}", f"#### Omitted {heading}")
                with self.assertRaisesRegex(ValueError, heading):
                    guide_documents([release], "owner/repo")

    def test_invalid_published_topic_does_not_write_partial_guides(self):
        invalid = self.release("v4.0.0")
        invalid["body"] = invalid["body"].replace("#### Recommended action", "#### Details")
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            with self.assertRaisesRegex(ValueError, "Recommended action"):
                write_guides(repo, [self.release(), invalid], "owner/repo")
            self.assertFalse((repo / "docs").exists())

    def test_version_introduced_comes_from_each_release_tag(self):
        for tag in ("v3.0.0", "v10.2.1"):
            with self.subTest(tag=tag):
                version = tag[1:]
                topic = guide_documents([self.release(tag)], "owner/repo")[version]["credential-helper-errors.md"]
                self.assertIn(f"**Version introduced:** {version}\n", topic)

    def test_multiple_topics_generate_individual_files_and_version_index(self):
        release = self.release()
        second = "<!-- migration-topic: registry-matching -->\n" + NOTE.replace(
            "Credential helper errors", "Registry matching"
        ) + "\n```markdown\n### This heading is a code example, not another topic\n```\n"
        release["body"] = release["body"].replace(MIGRATION_END, second + MIGRATION_END)
        documents = guide_documents([release], "owner/repo")["3.0.0"]
        self.assertEqual(set(documents), {"credential-helper-errors.md", "registry-matching.md", "README.md"})
        self.assertNotIn("Registry matching", documents["credential-helper-errors.md"])
        self.assertNotIn("Credential helper errors", documents["registry-matching.md"])
        self.assertIn("[Registry matching](registry-matching.md)", documents["README.md"])
        self.assertIn(
            "```markdown\n### This heading is a code example, not another topic\n```",
            documents["registry-matching.md"],
        )

    def test_topic_title_edit_preserves_filename(self):
        release = self.release()
        release["body"] = release["body"].replace("Credential helper errors", "New [title]")
        documents = guide_documents([release], "owner/repo")["3.0.0"]
        self.assertIn("credential-helper-errors.md", documents)
        self.assertIn("[New \\[title\\]](credential-helper-errors.md)", documents["README.md"])

    def test_unsafe_reserved_duplicate_and_missing_topic_markers_fail(self):
        for section in (
            "## Breaking changes and migration\n\n" + NOTE,
            "## Breaking changes and migration\n\n<!-- migration-topic: ../escape -->\n" + NOTE,
            "## Breaking changes and migration\n\n<!-- migration-topic: readme -->\n" + NOTE,
            "## Breaking changes and migration\n\n" + ("<!-- migration-topic: duplicate -->\n" + NOTE) * 2,
        ):
            with self.subTest(section=section), self.assertRaisesRegex(ValueError, "topic"):
                migration_topics(section)

    def test_skips_drafts_prereleases_and_releases_without_migration_notes(self):
        releases = [
            self.release(draft=True),
            self.release("v3.0.0-preview.1", prerelease=True),
            self.release(body="Legacy notes without migration markers"),
            self.release(body=None),
        ]
        self.assertEqual(guide_documents(releases, "owner/repo"), {})

    def test_malformed_markers_fail_instead_of_generating_partial_guide(self):
        for body in (
            MIGRATION_START, MIGRATION_END,
            f"{MIGRATION_END}\n{MIGRATION_START}",
            f"{MIGRATION_START}\n{MIGRATION_START}\n{MIGRATION_END}",
            f"{MIGRATION_START}\n## Breaking changes and migration\n{MIGRATION_END}",
        ):
            with self.subTest(body=body), self.assertRaisesRegex(ValueError, "migration notes"):
                migration_section(body)

    def test_windows_line_endings_preserve_section_text(self):
        release = self.release()
        expected = migration_section(release["body"])
        self.assertEqual(migration_section(release["body"].replace("\n", "\r\n")), expected)

    def test_unsafe_or_duplicate_version_fails(self):
        with self.assertRaisesRegex(ValueError, "tag"):
            guide_documents([self.release("v../escape")], "owner/repo")
        with self.assertRaisesRegex(ValueError, "Multiple"):
            guide_documents([self.release(), self.release()], "owner/repo")

    def test_index_orders_versions_numerically_and_descending(self):
        index = index_document({"3.0.0", "10.0.0", "4.0.0"})
        self.assertLess(index.index("10.0.0"), index.index("4.0.0"))
        self.assertLess(index.index("4.0.0"), index.index("3.0.0"))
        self.assertIn("[Upgrade to 3.0.0](3.0.0/README.md)", index)

    def test_generation_is_idempotent_and_retains_archived_guides(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            write_guides(repo, [self.release()], "owner/repo")
            files = sorted((repo / "docs/migrations").rglob("*.md"))
            before = {path.relative_to(repo): path.read_bytes() for path in files}
            write_guides(repo, [self.release()], "owner/repo")
            self.assertEqual(before, {path.relative_to(repo): path.read_bytes() for path in files})
            write_guides(repo, [self.release("v4.0.0")], "owner/repo")
            self.assertTrue((repo / "docs/migrations/3.0.0/credential-helper-errors.md").exists())
            index = (repo / "docs/migrations/README.md").read_text(encoding="utf-8")
            self.assertIn("3.0.0/README.md", index)
            self.assertIn("4.0.0/README.md", index)

    def test_corrected_release_updates_guide_without_duplicate_entries(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            write_guides(repo, [self.release()], "owner/repo")
            corrected = self.release()
            corrected["body"] = corrected["body"].replace("new exception type", "corrected exception type")
            write_guides(repo, [corrected], "owner/repo")
            guide = (repo / "docs/migrations/3.0.0/credential-helper-errors.md").read_text(encoding="utf-8")
            self.assertIn("corrected exception type", guide)
            self.assertEqual(guide.count("# Upgrade to 3.0.0"), 1)

    def test_removed_published_topic_is_removed_only_from_its_version(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            release = self.release()
            second = "<!-- migration-topic: removed-topic -->\n" + NOTE
            release["body"] = release["body"].replace(MIGRATION_END, second + MIGRATION_END)
            write_guides(repo, [release, self.release("v4.0.0")], "owner/repo")
            write_guides(repo, [self.release()], "owner/repo")
            self.assertFalse((repo / "docs/migrations/3.0.0/removed-topic.md").exists())
            self.assertTrue((repo / "docs/migrations/4.0.0/credential-helper-errors.md").exists())

    def test_reader_index_matches_checked_in_versioned_guides(self):
        versions = {path.parent.name for path in (ROOT / "docs/migrations").glob("*/README.md")}
        self.assertEqual(
            index_document(versions),
            (ROOT / "docs/migrations/README.md").read_text(encoding="utf-8"),
        )


if __name__ == "__main__":
    unittest.main()
