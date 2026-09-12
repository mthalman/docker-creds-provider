import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from migration_notes import check_pr, main, previous_tag, render
from update_release_draft import combine_notes, update_draft


ROOT = Path(__file__).resolve().parents[2]
NOTE = """### Credential helper errors

#### What changed

Malformed helper JSON now throws `InvalidOperationException`.

#### How to migrate

Catch the new exception type and inspect its inner exception.
"""


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
        self.write(f"docs/migrations/+{slug}.breaking.md", text)

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
        for text in ("", NOTE.replace("#### How to migrate", "#### Details"),
                     NOTE.split("Catch the new")[0] + "TODO\n"):
            with self.subTest(text=text):
                self.note(text=text)
                with self.assertRaisesRegex(ValueError, "migration fragment"):
                    check_pr(self.repo, self.base, self.commit(), ["semver:patch"])

    def test_invalid_fragment_filename_fails(self):
        self.write("docs/migrations/wrong.md", NOTE)
        with self.assertRaisesRegex(ValueError, "filename"):
            check_pr(self.repo, self.base, self.commit(), ["semver:major"])

    def test_deleting_or_renaming_note_fails(self):
        self.note()
        base = self.commit()
        self.git("mv", "docs/migrations/+helper.breaking.md", "docs/migrations/+renamed.breaking.md")
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
        self.assertTrue((self.repo / "docs/migrations/+new.breaking.md").exists())
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
        self.assertTrue(output.startswith("## Breaking changes and migration\n\n### "))
        self.assertIn("inner exception.\n\n### ", output)
        self.assertEqual(output, render(self.repo, None, head))

    def test_unicode_and_drafter_tokens_survive_full_markdown_pipeline(self):
        self.configure_renderer()
        text = NOTE + '\nOld \u2192 new; caf\u00e9.\n\n```sh\necho "$OWNER/$REPOSITORY/$RESOLVED_VERSION"\n```\n'
        self.note(text=text)
        notes = render(self.repo, None, self.commit())
        preview = "<!-- migration-base: v2.3.0 -->## What's Changed\n\n- Existing PR\n"
        combined = combine_notes(preview, notes)
        self.assertIn(text.strip(), combined)
        self.assertTrue(combined.endswith("```\n\n## What's Changed\n\n- Existing PR\n"))
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


if __name__ == "__main__":
    unittest.main()
