import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from migration_notes import MIGRATION_END, MIGRATION_START, TOPIC_MARKER_PREFIX, check_pr, main, previous_tag, render, shift_heading_levels, validate_fragment, validate_guide
from migration_guides import guide_documents, guides_ready, index_document, linked_notes, migration_topics, plan_guides, write_guides
from update_release_draft import combine_notes, process_preview, restore_complete_history, update_draft, verify_main


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

    def test_required_sections_must_follow_documented_order(self):
        title, *sections = NOTE.split("\n#### ")
        for index in range(len(sections) - 1):
            with self.subTest(first=REQUIRED_HEADINGS[index]):
                reordered = sections.copy()
                reordered[index], reordered[index + 1] = reordered[index + 1], reordered[index]
                text = "\n#### ".join([title, *reordered])
                with self.assertRaisesRegex(ValueError, "required sections must appear in this order"):
                    validate_fragment(".changes/+helper-errors.breaking.md", text)

    def test_each_required_section_must_have_completed_content(self):
        for heading in REQUIRED_HEADINGS:
            before, separator, after = NOTE.partition(f"#### {heading}\n")
            _, next_heading, remaining = after.partition("\n#### ")
            for content in (
                "", "TODO", "TBD.", "N/A", "<!-- Fill this in. -->",
                "##### Before", "###### After", "##### Before\n\n###### After",
                "   ##### Before ###", "#####\n\n######",
                "##### Before\n\n<!-- Fill this in. -->", "###### After\n\nTODO",
            ):
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

    def test_required_heading_order_ignores_fenced_examples(self):
        for fence in ("```", "~~~"):
            with self.subTest(fence=fence):
                headings = "\n".join(f"#### {heading}" for heading in reversed(REQUIRED_HEADINGS))
                text = NOTE.replace(
                    "#### Previous behavior",
                    f"{fence}markdown\n{headings}\n{fence}\n\n#### Previous behavior",
                    1,
                )
                self.validate(text)

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

    def test_nested_fenced_examples_are_valid(self):
        for fence in ("```", "~~~"):
            with self.subTest(fence=fence):
                text = NOTE.replace(
                    "Catch the new exception type and inspect its inner exception.",
                    f"##### Example\n\n{fence}markdown\n###### Heading inside example\n{fence}\n",
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
        self.git("init", "-q", "-b", "main")
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
        combined = combine_notes(preview, linked_notes(notes, "v3.0.0", "owner/repo"))
        self.assertNotIn(text.strip(), combined)
        self.assertTrue(combined.endswith(f"{MIGRATION_END}\n\n## What's Changed\n\n- Existing PR\n"))
        self.assertIn(
            text.split("\n", 1)[1].strip().replace("#### ", "## "),
            guide_documents(notes, "v3.0.0")["helper.md"],
        )
        self.assertNotIn("migration-base:", combined)

    def test_render_uses_committed_note_not_working_tree(self):
        self.configure_renderer()
        self.note()
        head = self.commit()
        self.note(text="Uncommitted invalid content")
        self.assertIn(NOTE.strip(), render(self.repo, self.base, head))

    def test_render_uses_selected_config_and_template(self):
        self.configure_renderer()
        self.note()
        head = self.commit()
        expected = render(self.repo, self.base, head)
        for name in ("towncrier.toml", ".github/migration-notes.md.jinja"):
            original = (self.repo / name).read_text(encoding="utf-8")
            for committed in (False, True):
                with self.subTest(file=name, committed=committed):
                    self.write(name, "Invalid content outside the selected commit.")
                    if committed:
                        self.commit()
                    try:
                        self.assertEqual(render(self.repo, self.base, head), expected)
                    finally:
                        self.write(name, original)

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
        for text in (
            "TODO",
            NOTE.replace("Catch the new exception type and inspect its inner exception.", "##### Before"),
        ):
            with self.subTest(text=text):
                self.note(text=text)
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

    def test_release_links_wait_for_exact_merged_guides(self):
        self.configure_renderer()
        self.note("helper", NOTE + '\n```sh\necho "$OWNER"\n```\n')
        head = self.commit()
        notes = render(self.repo, self.base, head)
        plan = plan_guides(self.repo, head, notes, "v3.0.0", [])
        topic_path = "docs/migrations/3.0.0/helper.md"
        self.assertIn(NOTE.split("\n", 1)[1].strip().replace("#### ", "## "), plan[topic_path])
        self.assertIn('echo "$OWNER"', plan[topic_path])
        self.assertIn("**Version introduced:** 3.0.0", plan[topic_path])
        self.assertNotIn("/releases/tag/", plan[topic_path])
        self.assertFalse(guides_ready(self.repo, head, plan))
        for path, text in plan.items():
            self.write(path, text)
        self.assertFalse(guides_ready(self.repo, head, plan), "Uncommitted guides cannot be linked")
        merged = self.commit()
        self.assertTrue(guides_ready(self.repo, merged, plan))
        summary = linked_notes(notes, "v3.0.0", "owner/repo")
        self.assertIn("[Credential helper errors](https://github.com/owner/repo/blob/main/docs/migrations/3.0.0/helper.md)", summary)
        self.assertNotIn("#### Previous behavior", summary)
        self.assertNotIn('echo "$OWNER"', summary)
        self.assertEqual(plan, plan_guides(self.repo, merged, notes, "v3.0.0", []))
        corrected = notes.replace("Malformed helper JSON now", "Invalid helper JSON now")
        revised = plan_guides(self.repo, merged, corrected, "v3.0.0", [])
        self.assertFalse(guides_ready(self.repo, merged, revised))

    def test_version_change_replaces_only_unpublished_guides(self):
        self.configure_renderer()
        self.note()
        head = self.commit()
        notes = render(self.repo, self.base, head)
        first = plan_guides(self.repo, head, notes, "v3.0.0", [])
        for path, text in first.items():
            self.write(path, text)
        merged = self.commit()
        next_plan = plan_guides(self.repo, merged, notes, "v4.0.0", [])
        self.assertNotIn("docs/migrations/3.0.0/helper.md", next_plan)
        self.assertIn("docs/migrations/4.0.0/helper.md", next_plan)
        self.assertNotIn("3.0.0/README.md", next_plan["docs/migrations/README.md"])
        published = [{"tag_name": "v3.0.0", "draft": False, "prerelease": False}]
        retained = plan_guides(self.repo, merged, notes, "v4.0.0", published)
        self.assertEqual(retained["docs/migrations/3.0.0/helper.md"], first["docs/migrations/3.0.0/helper.md"])
        self.assertIn("3.0.0/README.md", retained["docs/migrations/README.md"])

    def test_version_change_keeps_existing_draft_links_until_replaced(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        first = plan_guides(self.repo, self.base, notes, "v3.0.0", [])
        write_guides(self.repo, self.base, first)
        merged = self.commit()
        draft = {
            "tag_name": "v3.0.0", "draft": True, "prerelease": False,
            "body": linked_notes(notes, "v3.0.0", "owner/repo"),
        }
        changed_version = plan_guides(self.repo, merged, notes, "v4.0.0", [draft])
        self.assertIn("docs/migrations/3.0.0/helper.md", changed_version)
        self.assertIn("docs/migrations/4.0.0/helper.md", changed_version)
        write_guides(self.repo, merged, changed_version)
        new_merged = self.commit()
        self.assertEqual(
            plan_guides(self.repo, new_merged, notes, "v4.0.0", [draft]), changed_version
        )
        draft["tag_name"] = "v4.0.0"
        draft["body"] = linked_notes(notes, "v4.0.0", "owner/repo")
        cleanup = plan_guides(self.repo, new_merged, notes, "v4.0.0", [draft])
        self.assertNotIn("docs/migrations/3.0.0/helper.md", cleanup)
        self.assertIn("docs/migrations/4.0.0/helper.md", cleanup)

    def test_published_corrections_survive_subsequent_generation(self):
        self.configure_renderer()
        self.note()
        head = self.commit()
        notes = render(self.repo, self.base, head)
        first = plan_guides(self.repo, head, notes, "v3.0.0", [])
        for path, text in first.items():
            self.write(path, text)
        path = "docs/migrations/3.0.0/helper.md"
        corrected = first[path].replace("new exception type", "documented exception type")
        self.write(path, corrected)
        merged = self.commit()
        published = [{"tag_name": "v3.0.0", "draft": False, "prerelease": False}]
        retained = plan_guides(self.repo, merged, "", "v3.0.1", published)
        self.assertEqual(retained[path], corrected)
        self.assertNotIn("docs/migrations/3.0.1/README.md", retained)
        self.assertEqual(linked_notes("", "v3.0.1", "owner/repo"), "")
        write_guides(self.repo, merged, retained)
        next_merged = self.commit()
        self.assertEqual(
            plan_guides(self.repo, next_merged, "", "v4.0.0", [])[path], corrected,
            "Previously published guides remain even if a release is no longer listed",
        )

    def test_preview_lifecycle_never_writes_draft_until_guides_merge(self):
        self.configure_renderer()
        self.git("remote", "add", "origin", str(self.repo))
        self.git("tag", "v2.3.0", self.base)
        self.note(text=NOTE.replace("Credential helper errors", "Credential $OWNER errors"))
        head = self.commit()
        preview = "<!-- migration-base: v2.3.0 -->## What's Changed\n\n- Product fix"
        writes = []

        def fake_api(endpoint, method="GET", payload=None):
            if method == "GET":
                return []
            writes.append((endpoint, method, payload))
            return {**payload, "id": 123, "published_at": None}

        with patch("update_release_draft.api", side_effect=fake_api):
            for _ in range(2):
                self.assertFalse(process_preview(
                    self.repo, head, preview, "Release 3.0.0", "v3.0.0",
                    "owner/repo", [], prepare=True,
                ))
                with self.assertRaisesRegex(ValueError, "Waiting for migration guides"):
                    process_preview(
                        self.repo, head, preview, "Release 3.0.0", "v3.0.0",
                        "owner/repo", [], prepare=False,
                    )
            self.assertEqual(writes, [])
            merged = self.commit()
            self.assertTrue(process_preview(
                self.repo, merged, preview, "Release 3.0.0", "v3.0.0",
                "owner/repo", [], prepare=True,
            ))
            self.assertTrue(process_preview(
                self.repo, merged, preview, "Release 3.0.0", "v3.0.0",
                "owner/repo", [], prepare=False,
            ))
        self.assertEqual(len(writes), 1)
        endpoint, method, payload = writes[0]
        self.assertEqual((endpoint, method), ("repos/owner/repo/releases", "POST"))
        self.assertEqual(payload["target_commitish"], merged)
        self.assertTrue(payload["draft"])
        self.assertFalse(payload["prerelease"])
        self.assertIn(
            "[Credential $OWNER errors](https://github.com/owner/repo/blob/main/docs/migrations/3.0.0/helper.md)",
            payload["body"],
        )
        self.assertNotIn("#### Previous behavior", payload["body"])
        self.assertTrue(payload["body"].endswith("## What's Changed\n\n- Product fix"))
        self.assertNotIn("migration-base:", payload["body"])
        self.assertEqual(self.git("status", "--porcelain"), "")
        with patch("update_release_draft.api") as api:
            with self.assertRaisesRegex(ValueError, "main changed"):
                process_preview(
                    self.repo, head, preview, "Release 3.0.0", "v3.0.0",
                    "owner/repo", [], prepare=False,
                )
            api.assert_not_called()

    def test_invalid_preview_does_not_write_partial_guides(self):
        self.configure_renderer()
        self.git("remote", "add", "origin", str(self.repo))
        self.note()
        head = self.commit()
        with patch("update_release_draft.api", return_value=[]):
            with self.assertRaisesRegex(ValueError, "no release notes"):
                process_preview(
                    self.repo, head, "<!-- migration-base:  -->", "Release 3.0.0",
                    "v3.0.0", "owner/repo", [], prepare=True,
                )
        self.assertFalse((self.repo / "docs").exists())

    def test_preview_rechecks_releases_and_main_after_guide_verification(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        self.configure_renderer()
        self.note()
        head = self.commit()
        write_guides(self.repo, head, plan_guides(self.repo, head, notes, "v3.0.0", []))
        merged = self.commit()
        preview = "<!-- migration-base:  -->## What's Changed\n\n- Product fix"
        new_draft = {
            "id": 10, "tag_name": "v3.0.0", "name": "3.0.0", "draft": True,
            "prerelease": False, "target_commitish": merged, "body": "Concurrent edit",
            "updated_at": "2026-09-02", "published_at": None,
        }
        with patch("update_release_draft.verify_main"), \
                patch("update_release_draft.api", side_effect=[[], [new_draft]]) as api:
            with self.assertRaisesRegex(ValueError, "Releases changed"):
                process_preview(
                    self.repo, merged, preview, "3.0.0", "v3.0.0",
                    "owner/repo", [], prepare=False,
                )
        self.assertEqual(len(api.call_args_list), 2)
        self.assertTrue(all(len(call.args) == 1 for call in api.call_args_list))
        with patch("update_release_draft.verify_main", side_effect=[
            None, ValueError("main changed during generation"),
        ]), patch("update_release_draft.api", return_value=[]) as api:
            with self.assertRaisesRegex(ValueError, "main changed"):
                process_preview(
                    self.repo, merged, preview, "3.0.0", "v3.0.0",
                    "owner/repo", [], prepare=False,
                )
        api.assert_called_once()

    def test_draft_update_restores_history_after_documentation_branch_fetch(self):
        self.configure_renderer()
        self.git("tag", "v2.3.0", self.base)
        self.note()
        self.commit()
        for _ in range(12):
            self.git("commit", "--allow-empty", "-qm", "Additional product change")
        head = self.git("rev-parse", "HEAD")
        notes = render(self.repo, "refs/tags/v2.3.0", head)
        write_guides(self.repo, head, plan_guides(self.repo, head, notes, "v3.0.0", []))
        merged = self.commit()
        self.git("checkout", "-qb", "automation/migration-guides")
        self.write(
            "docs/migrations/3.0.0/helper.md",
            guide_documents(notes, "v3.0.0")["helper.md"] + "\nStale proposed correction.\n",
        )
        self.commit()
        self.git("checkout", "-q", "main")
        with tempfile.TemporaryDirectory() as directory:
            clone = Path(directory) / "checkout"
            subprocess.run(
                ["git", "clone", "--quiet", "--no-local", str(self.repo), str(clone)],
                check=True,
            )
            preview = "<!-- migration-base: v2.3.0 -->## What's Changed\n\n- Product fix"
            with patch("update_release_draft.api", return_value=[]):
                self.assertTrue(process_preview(
                    clone, merged, preview, "3.0.0", "v3.0.0", "owner/repo", [], prepare=True,
                ))
            subprocess.run(
                ["git", "fetch", "--force", "--depth=10", "origin",
                 "automation/migration-guides:refs/remotes/origin/automation/migration-guides"],
                cwd=clone, check=True,
            )
            self.assertEqual(subprocess.check_output(
                ["git", "rev-parse", "--is-shallow-repository"], cwd=clone, text=True
            ).strip(), "true")
            writes = []

            def fake_api(endpoint, method="GET", payload=None):
                if method == "GET":
                    return []
                writes.append(payload)
                return {**payload, "id": 123, "published_at": None}

            with patch("update_release_draft.api", side_effect=fake_api):
                self.assertTrue(process_preview(
                    clone, merged, preview, "3.0.0", "v3.0.0", "owner/repo", [], prepare=False,
                ))
            self.assertEqual(len(writes), 1)
            self.assertEqual(writes[0]["target_commitish"], merged)
            self.assertEqual(subprocess.check_output(
                ["git", "rev-parse", "--is-shallow-repository"], cwd=clone, text=True
            ).strip(), "false")

    def test_version_cleanup_is_idempotent_and_preserves_other_files(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        first = plan_guides(self.repo, self.base, notes, "v3.0.0", [])
        write_guides(self.repo, self.base, first)
        self.write("docs/unrelated.md", "Keep this document.\n")
        merged = self.commit()
        next_plan = plan_guides(self.repo, merged, notes, "v4.0.0", [])
        write_guides(self.repo, merged, next_plan)
        self.assertFalse((self.repo / "docs/migrations/3.0.0/helper.md").exists())
        self.assertTrue((self.repo / "docs/migrations/4.0.0/helper.md").exists())
        self.assertEqual((self.repo / "docs/unrelated.md").read_text(), "Keep this document.\n")
        next_merged = self.commit()
        check_pr(self.repo, merged, next_merged, ["semver:patch"])
        self.assertEqual(plan_guides(self.repo, next_merged, notes, "v4.0.0", []), next_plan)

    def test_no_topics_removes_only_superseded_pending_version(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        write_guides(self.repo, self.base, plan_guides(self.repo, self.base, notes, "v3.0.0", []))
        merged = self.commit()
        plan = plan_guides(self.repo, merged, "", "v3.0.0", [])
        self.assertNotIn("docs/migrations/3.0.0/helper.md", plan)
        self.assertNotIn("3.0.0/README.md", plan["docs/migrations/README.md"])
        self.assertEqual(json.loads(plan[".github/migration-guides.json"])["pending_versions"], [])

    def test_invalid_state_or_published_version_stops_generation(self):
        for state in (
            [], {"pending_versions": ["../escape"]}, {"pending_versions": 3},
            {"pending_versions": ["03.0.0"]}, {"unexpected": "3.0.0"},
            {"pending_versions": ["3.0.0", "3.0.0"]}, {"pending_versions": [3]},
        ):
            with self.subTest(state=state):
                self.write(".github/migration-guides.json", json.dumps(state))
                with self.assertRaisesRegex(ValueError, "migration guide"):
                    plan_guides(self.repo, self.commit(), "", "v3.0.0", [])
        self.write(".github/migration-guides.json", '{"pending_versions": []}\n')
        with self.assertRaisesRegex(ValueError, "already published"):
            plan_guides(self.repo, self.commit(), "", "v3.0.0", [
                {"tag_name": "v3.0.0", "draft": False, "prerelease": False},
            ])

    def test_unmanaged_version_is_not_silently_overwritten(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        for name, text in guide_documents(notes, "v3.0.0").items():
            self.write(f"docs/migrations/3.0.0/{name}", text)
        with self.assertRaisesRegex(ValueError, "Refusing to overwrite"):
            plan_guides(self.repo, self.commit(), notes, "v3.0.0", [])

    def test_policy_validates_published_corrections_and_exempts_indexes(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        for name, text in guide_documents(notes, "v3.0.0").items():
            self.write(f"docs/migrations/3.0.0/{name}", text)
        self.write("docs/migrations/README.md", "Version navigation\n")
        base = self.commit()
        check_pr(self.repo, self.base, base, ["semver:patch"])
        text = (self.repo / "docs/migrations/3.0.0/helper.md").read_text()
        self.write("docs/migrations/3.0.0/helper.md", text.replace(
            "Catch the new exception type and inspect its inner exception.", "### Before"
        ))
        with self.assertRaisesRegex(ValueError, "Recommended action"):
            check_pr(self.repo, base, self.commit(), ["semver:patch"])

    def test_policy_rejects_deleting_retained_guides(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        for pending in (None, [], ["4.0.0"]):
            for target in (
                "docs/migrations/3.0.0/helper.md",
                "docs/migrations/3.0.0/README.md",
                "docs/migrations/3.0.0",
            ):
                with self.subTest(pending=pending, target=target):
                    for name, text in guide_documents(notes, "v3.0.0").items():
                        self.write(f"docs/migrations/3.0.0/{name}", text)
                    if pending is None:
                        (self.repo / ".github/migration-guides.json").unlink(missing_ok=True)
                    else:
                        self.write(".github/migration-guides.json", json.dumps({
                            "pending_versions": pending,
                        }))
                    base = self.commit()
                    self.git("rm", "-r", "--", target)
                    with self.assertRaisesRegex(ValueError, "Cannot delete or rename"):
                        check_pr(self.repo, base, self.commit(), ["semver:patch"])

    def test_policy_rejects_deleting_root_index_even_with_pending_guides(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        write_guides(self.repo, self.base, plan_guides(self.repo, self.base, notes, "v3.0.0", []))
        base = self.commit()
        self.git("rm", "--", "docs/migrations/README.md")
        with self.assertRaisesRegex(ValueError, "Cannot delete or rename"):
            check_pr(self.repo, base, self.commit(), ["semver:patch"])

    def test_policy_rejects_pending_authorization_added_only_in_pr(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        for name, text in guide_documents(notes, "v3.0.0").items():
            self.write(f"docs/migrations/3.0.0/{name}", text)
        base = self.commit()
        self.write(".github/migration-guides.json", '{"pending_versions": ["3.0.0"]}\n')
        self.git("rm", "-r", "--", "docs/migrations/3.0.0")
        with self.assertRaisesRegex(ValueError, "Cannot delete or rename"):
            check_pr(self.repo, base, self.commit(), ["semver:patch"])

    def test_policy_uses_current_base_not_old_pending_state_at_merge_base(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        write_guides(self.repo, self.base, plan_guides(self.repo, self.base, notes, "v3.0.0", []))
        old_base = self.commit()
        write_guides(self.repo, old_base, plan_guides(self.repo, old_base, notes, "v4.0.0", []))
        head = self.commit()
        self.git("checkout", "-qb", "published-guides", old_base)
        self.write(".github/migration-guides.json", '{"pending_versions": []}\n')
        current_base = self.commit()
        with self.assertRaisesRegex(ValueError, "Cannot delete or rename"):
            check_pr(self.repo, current_base, head, ["semver:patch"])

    def test_policy_rejects_malformed_base_state_before_allowing_deletion(self):
        notes = "## Breaking changes and migration\n\n<!-- migration-topic: helper -->\n" + NOTE
        for state, error in (
            ('{"pending_versions": ["3.0.0", "../escape"]}', "migration guide"),
            ("{not json", "Expecting property name"),
        ):
            with self.subTest(state=state):
                for name, text in guide_documents(notes, "v3.0.0").items():
                    self.write(f"docs/migrations/3.0.0/{name}", text)
                self.write(".github/migration-guides.json", state)
                base = self.commit()
                self.write(".github/migration-guides.json", '{"pending_versions": ["3.0.0"]}\n')
                self.git("rm", "--", "docs/migrations/3.0.0/helper.md")
                with self.assertRaisesRegex(ValueError, error):
                    check_pr(self.repo, base, self.commit(), ["semver:patch"])

    def test_main_verification_rejects_missing_or_different_remote_head(self):
        for output in ("", "other-sha\trefs/heads/main\n"):
            with self.subTest(output=output), patch("update_release_draft.git", return_value=output):
                with self.assertRaisesRegex(ValueError, "main changed"):
                    verify_main(self.repo, self.base)


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

    def test_unrelated_drafts_block_creation_and_updates_without_writing(self):
        for tag, prerelease in (
            ("v-next", False), ("v3.0.0-preview.1", False), ("v03.0.0", False),
            ("unrelated", False), ("v3.0.0-preview.1", True), ("v3.0.0", True),
        ):
            for has_stable_draft in (False, True):
                with self.subTest(tag=tag, prerelease=prerelease, has_stable_draft=has_stable_draft):
                    unrelated = {**self.draft, "id": 11, "tag_name": tag, "prerelease": prerelease}
                    drafts = [unrelated, self.draft] if has_stable_draft else [unrelated]
                    with patch("update_release_draft.api",
                               side_effect=[drafts, self.result()]) as api:
                        with self.assertRaisesRegex(ValueError, "Unrelated release drafts"):
                            self.update(drafts)
                    api.assert_called_once_with(self.endpoint)

    def test_published_releases_do_not_block_drafting(self):
        for prerelease in (False, True):
            with self.subTest(prerelease=prerelease):
                published = {
                    **self.draft, "id": 11, "draft": False, "prerelease": prerelease,
                    "tag_name": "v2.3.0-preview.1" if prerelease else "v2.3.0",
                    "published_at": "2026-09-01",
                }
                with patch("update_release_draft.api",
                           side_effect=[[published], self.result()]) as api:
                    self.update([published])
                self.assertEqual(api.call_args_list[-1].args[:2], (self.endpoint, "POST"))

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

    def test_published_target_version_aborts_without_writing(self):
        published = {**self.draft, "draft": False, "published_at": "2026-09-02"}
        with patch("update_release_draft.api", return_value=[published]) as api:
            with self.assertRaisesRegex(ValueError, "already published"):
                self.update([published])
        api.assert_called_once_with(self.endpoint)

    def test_empty_migration_output_does_not_add_section(self):
        self.assertEqual(
            combine_notes("<!-- migration-base: v2.3.0 -->## What's Changed", ""),
            "## What's Changed",
        )

    def test_complete_history_does_not_fetch_again(self):
        with patch("update_release_draft.git", return_value="false\n") as git:
            restore_complete_history(ROOT)
        git.assert_called_once_with(ROOT, "rev-parse", "--is-shallow-repository")

    def test_failed_history_restore_prevents_draft_write(self):
        failure = subprocess.CalledProcessError(1, ["git", "fetch", "--unshallow", "origin"])
        with patch("update_release_draft.verify_main"), \
                patch("update_release_draft.git", side_effect=["true\n", failure]), \
                patch("update_release_draft.api", return_value=[]) as api:
            with self.assertRaises(subprocess.CalledProcessError):
                process_preview(
                    ROOT, "commit", "<!-- migration-base: v2.3.0 -->## Changes",
                    "3.0.0", "v3.0.0", "owner/repo", [], prepare=False,
                )
        api.assert_called_once_with(self.endpoint)


class MarkdownHeadingTests(unittest.TestCase):
    def test_shifting_preserves_heading_spacing_and_line_endings(self):
        text = "   ####\tTitle ###\r\n\r\n    #### Indented code\r\n#####\r\n"
        expected = "   ##\tTitle ###\r\n\r\n    #### Indented code\r\n###\r\n"
        self.assertEqual(shift_heading_levels(text, -2), expected)
        self.assertEqual(shift_heading_levels(expected, 2), text)

    def test_shifting_rejects_levels_outside_markdown_range(self):
        for text, offset in (("## Too shallow\n", -2), ("##### Too deep\n", 2)):
            with self.subTest(text=text), self.assertRaisesRegex(ValueError, "Cannot shift Markdown heading"):
                shift_heading_levels(text, offset)


class MigrationGuideTests(unittest.TestCase):
    def notes(self, text=NOTE):
        return (
            "## Breaking changes and migration\n\n"
            "<!-- migration-topic: credential-helper-errors -->\n" + text
        )

    def test_guide_contains_full_topic_without_future_release_link(self):
        guides = guide_documents(self.notes(), "v3.0.0")
        topic = guides["credential-helper-errors.md"]
        self.assertEqual(set(guides), {"credential-helper-errors.md", "README.md"})
        self.assertEqual(
            topic,
            "# Credential helper errors\n\n**Version introduced:** 3.0.0\n\n"
            + NOTE.split("\n", 1)[1].lstrip("\n").replace("#### ", "## "),
        )
        self.assertIn("**Version introduced:** 3.0.0\n", topic)
        self.assertNotIn("/releases/tag/", topic)
        self.assertNotIn("What's Changed", topic)
        self.assertNotIn("migration-notes:", topic)
        self.assertNotIn("migration-topic:", topic)
        self.assertTrue(guides["README.md"].startswith("# Upgrade to 3.0.0\n\n"))
        self.assertIn("[Credential helper errors](credential-helper-errors.md)", guides["README.md"])

    def test_topic_introduction_is_preserved_without_repeating_title(self):
        introduction = "This change affects applications that invoke credential helpers."
        notes = self.notes(NOTE.replace(
            "### Credential helper errors\n", f"### Credential helper errors\n\n{introduction}\n"
        ))
        topic = guide_documents(notes, "v3.0.0")["credential-helper-errors.md"]
        self.assertIn(f"**Version introduced:** 3.0.0\n\n{introduction}\n\n## Previous behavior", topic)
        self.assertEqual(topic.count("Credential helper errors"), 1)
        validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", topic)

    def test_existing_guide_format_remains_valid(self):
        topic = (
            "# Upgrade to 3.0.0\n\n**Version introduced:** 3.0.0\n\n"
            "## Breaking changes and migration\n\n" + NOTE
        )
        for preamble in ("", "```markdown\n## Breaking changes and migration\n```\n\n"):
            with self.subTest(preamble=preamble):
                validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", preamble + topic)
        with self.assertRaisesRegex(ValueError, "Recommended action"):
            validate_guide(
                "docs/migrations/3.0.0/credential-helper-errors.md",
                topic.replace("#### Recommended action", "#### Details"),
            )

    def test_versioned_topics_require_the_same_sections_as_fragments(self):
        for heading in REQUIRED_HEADINGS:
            with self.subTest(heading=heading):
                topic = guide_documents(self.notes(), "v3.0.0")["credential-helper-errors.md"]
                topic = topic.replace(f"## {heading}", f"## Omitted {heading}")
                with self.assertRaisesRegex(ValueError, heading):
                    validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", topic)

    def test_standalone_topics_require_level_two_sections(self):
        for heading in REQUIRED_HEADINGS:
            with self.subTest(heading=heading):
                topic = guide_documents(self.notes(), "v3.0.0")["credential-helper-errors.md"]
                topic = topic.replace(f"## {heading}", f"#### {heading}")
                with self.assertRaisesRegex(ValueError, heading):
                    validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", topic)

    def test_standalone_topics_require_documented_section_order(self):
        topic = guide_documents(self.notes(), "v3.0.0")["credential-helper-errors.md"]
        title, *sections = topic.split("\n## ")
        reordered = "\n## ".join([title, sections[1], sections[0], *sections[2:]])
        with self.assertRaisesRegex(ValueError, "required sections must appear in this order"):
            validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", reordered)

    def test_versioned_topic_heading_must_be_outside_fences(self):
        topic = guide_documents(self.notes(), "v3.0.0")["credential-helper-errors.md"]
        for fence in ("```", "~~~~", "   ```"):
            with self.subTest(fence=fence):
                with self.assertRaisesRegex(ValueError, "heading"):
                    validate_guide(
                        "docs/migrations/3.0.0/credential-helper-errors.md",
                        f"{fence}markdown\n{topic}{fence}\n",
                    )

    def test_versioned_topic_ignores_migration_heading_in_fenced_preamble(self):
        topic = guide_documents(self.notes(), "v3.0.0")["credential-helper-errors.md"]
        preamble = "```markdown\n## Breaking changes and migration\n\n### Example only\n```\n\n"
        validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", preamble + topic)

    def test_fenced_preamble_cannot_supply_missing_topic_sections(self):
        topic = guide_documents(self.notes(), "v3.0.0")["credential-helper-errors.md"]
        invalid_topic = topic.replace("## Recommended action", "## Details")
        with self.assertRaisesRegex(ValueError, "Recommended action"):
            validate_guide(
                "docs/migrations/3.0.0/credential-helper-errors.md",
                f"```markdown\n{topic}```\n\n{invalid_topic}",
            )

    def test_topics_require_documented_section_order(self):
        title, *sections = NOTE.split("\n#### ")
        reordered = "\n#### ".join([title, sections[-1], *sections[:-1]])
        with self.assertRaisesRegex(ValueError, "required sections must appear in this order"):
            guide_documents(self.notes(reordered), "v3.0.0")

    def test_topics_reject_missing_or_heading_only_sections(self):
        for original, replacement in (
            ("#### Recommended action", "#### Details"),
            ("Catch the new exception type and inspect its inner exception.", "##### Before"),
        ):
            with self.subTest(replacement=replacement):
                with self.assertRaisesRegex(ValueError, "Recommended action"):
                    guide_documents(self.notes(NOTE.replace(original, replacement)), "v3.0.0")

    def test_populated_nested_headings_are_preserved_in_guides(self):
        section = "##### Before\n\nUse the old API.\n\n###### After\n\n```csharp\nNewApi();\n```"
        notes = self.notes().replace(
            "Catch the new exception type and inspect its inner exception.", section
        )
        topic = guide_documents(notes, "v3.0.0")["credential-helper-errors.md"]
        self.assertIn("### Before\n\nUse the old API.\n\n#### After\n\n```csharp\nNewApi();\n```", topic)
        self.assertEqual(
            [line for line in topic.splitlines() if line.startswith("#")],
            [
                "# Credential helper errors",
                "## Previous behavior",
                "## New behavior",
                "## Type of breaking change",
                "## Reason for change",
                "## Recommended action",
                "### Before",
                "#### After",
                "## Affected APIs",
            ],
        )
        validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", topic)

    def test_heading_promotion_preserves_fenced_markdown(self):
        for fence, content, close in (
            ("```", "#### Example\n##### Nested example", "```"),
            ("~~~", "#### Example\n##### Nested example", "~~~"),
            ("````", "```\n#### Still fenced\n~~~~\n##### Still fenced", "````"),
            ("   ```", "#### Example\n##### Nested example", "   ````"),
        ):
            with self.subTest(fence=fence):
                example = f"{fence}markdown\n{content}\n{close}"
                section = f"##### Before\n\n{example}\n\n###### After\n\nUse the new API."
                notes = self.notes().replace(
                    "Catch the new exception type and inspect its inner exception.", section
                )
                topic = guide_documents(notes, "v3.0.0")["credential-helper-errors.md"]
                self.assertIn(f"### Before\n\n{example}\n\n#### After\n\nUse the new API.", topic)
                validate_guide("docs/migrations/3.0.0/credential-helper-errors.md", topic)

    def test_version_introduced_comes_from_computed_tag(self):
        for tag in ("v3.0.0", "v10.2.1"):
            with self.subTest(tag=tag):
                version = tag[1:]
                topic = guide_documents(self.notes(), tag)["credential-helper-errors.md"]
                self.assertIn(f"**Version introduced:** {version}\n", topic)

    def test_multiple_topics_generate_individual_files_and_version_index(self):
        second = "<!-- migration-topic: registry-matching -->\n" + NOTE.replace(
            "Credential helper errors", "Registry matching"
        ) + "\n```markdown\n### This heading is a code example, not another topic\n```\n"
        documents = guide_documents(self.notes() + "\n" + second, "v3.0.0")
        self.assertEqual(set(documents), {"credential-helper-errors.md", "registry-matching.md", "README.md"})
        self.assertTrue(documents["credential-helper-errors.md"].startswith("# Credential helper errors\n\n"))
        self.assertTrue(documents["registry-matching.md"].startswith("# Registry matching\n\n"))
        self.assertNotIn("Registry matching", documents["credential-helper-errors.md"])
        self.assertNotIn("Credential helper errors", documents["registry-matching.md"])
        self.assertIn("[Registry matching](registry-matching.md)", documents["README.md"])
        self.assertIn(
            "```markdown\n### This heading is a code example, not another topic\n```",
            documents["registry-matching.md"],
        )

    def test_topic_title_edit_preserves_filename(self):
        notes = self.notes().replace("Credential helper errors", "New [title]")
        documents = guide_documents(notes, "v3.0.0")
        self.assertIn("credential-helper-errors.md", documents)
        self.assertTrue(documents["credential-helper-errors.md"].startswith("# New [title]\n\n"))
        self.assertIn("[New \\[title\\]](credential-helper-errors.md)", documents["README.md"])
        self.assertIn("[New \\[title\\]](", linked_notes(notes, "v3.0.0", "owner/repo"))

    def test_unsafe_reserved_duplicate_and_missing_topic_markers_fail(self):
        for section in (
            "## Breaking changes and migration\n\n" + NOTE,
            "## Breaking changes and migration\n\n<!-- migration-topic: ../escape -->\n" + NOTE,
            "## Breaking changes and migration\n\n<!-- migration-topic: readme -->\n" + NOTE,
            "## Breaking changes and migration\n\n" + ("<!-- migration-topic: duplicate -->\n" + NOTE) * 2,
        ):
            with self.subTest(section=section), self.assertRaisesRegex(ValueError, "topic"):
                migration_topics(section)

    def test_windows_line_endings_preserve_section_text(self):
        expected = guide_documents(self.notes(), "v3.0.0")
        self.assertEqual(guide_documents(self.notes().replace("\n", "\r\n"), "v3.0.0"), expected)

    def test_unsafe_or_prerelease_version_fails(self):
        for tag in ("v../escape", "v3.0.0-preview.1", "v03.0.0"):
            with self.subTest(tag=tag), self.assertRaisesRegex(ValueError, "tag"):
                guide_documents(self.notes(), tag)

    def test_index_orders_versions_numerically_and_descending(self):
        index = index_document({"3.0.0", "10.0.0", "4.0.0"})
        self.assertLess(index.index("10.0.0"), index.index("4.0.0"))
        self.assertLess(index.index("4.0.0"), index.index("3.0.0"))
        self.assertIn("[Upgrade to 3.0.0](3.0.0/README.md)", index)

    def test_reader_index_matches_checked_in_versioned_guides(self):
        versions = {path.parent.name for path in (ROOT / "docs/migrations").glob("*/README.md")}
        self.assertEqual(
            index_document(versions),
            (ROOT / "docs/migrations/README.md").read_text(encoding="utf-8"),
        )

    def test_repository_topics_follow_format(self):
        for path in (ROOT / "docs/migrations").rglob("*.md"):
            with self.subTest(path=path):
                validate_guide(path.relative_to(ROOT).as_posix(), path.read_text(encoding="utf-8"))

    def test_existing_helper_guidance_is_preserved_without_inline_details(self):
        source = (ROOT / ".changes/+credential-helper-errors.breaking.md").read_text(encoding="utf-8")
        notes = self.notes(source)
        topic = guide_documents(notes, "v3.0.0")["credential-helper-errors.md"]
        self.assertIn(source.split("\n", 1)[1].strip().replace("#### ", "## "), topic)
        self.assertEqual(topic.count("Credential-helper failures use sanitized exceptions"), 1)
        summary = linked_notes(notes, "v3.0.0", "owner/repo")
        self.assertIn("Credential-helper failures use sanitized exceptions", summary)
        self.assertNotIn("InnerException", summary)
        self.assertEqual(len(summary.strip().splitlines()), 3)


if __name__ == "__main__":
    unittest.main()
