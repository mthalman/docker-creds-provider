import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]
TOOLKIT = "mthalman/release-automation"
PUBLISH_GUARD = "steps.prepare.outputs.already-published != 'true'"


def workflow(name):
    return yaml.load(
        (ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8"),
        Loader=yaml.BaseLoader,
    )


class ReleaseAutomationTests(unittest.TestCase):
    def setUp(self):
        self.policy = workflow("migration-note-policy.yml")
        self.draft = workflow("release-drafter.yml")
        self.release = workflow("release.yml")
        self.prepare = self.release["jobs"]["prepare"]
        self.build = self.release["jobs"]["build"]
        self.publish = self.release["jobs"]["publish"]
        self.steps = self.prepare["steps"] + self.build["steps"] + self.publish["steps"]

    def step(self, name):
        return next(step for step in self.steps if step["name"] == name)

    def test_all_entrypoints_share_one_immutable_release_pin(self):
        references = []
        for path in (ROOT / ".github" / "workflows").glob("*.yml"):
            for line in path.read_text(encoding="utf-8").splitlines():
                if f"uses: {TOOLKIT}/" in line:
                    match = re.fullmatch(
                        rf"\s+uses: {TOOLKIT}/(\S+)@([0-9a-f]{{40}}) # (v\d+\.\d+\.\d+)",
                        line,
                    )
                    self.assertIsNotNone(match, line)
                    references.append(match.groups())
        self.assertCountEqual(
            [entrypoint for entrypoint, _, _ in references],
            [
                ".github/workflows/migration-policy.yml",
                ".github/workflows/release-draft.yml",
                "actions/prepare-release",
                "actions/prepare-release",
                "actions/finalize-release",
            ],
        )
        self.assertEqual(len({(sha, tag) for _, sha, tag in references}), 1)
        sha = references[0][1]
        links = []
        for name in ("AGENTS.md", "CONTRIBUTING.md", "MAINTAINERS.md"):
            links.extend(re.findall(
                rf"https://github.com/{TOOLKIT}/(?:blob|tree)/([^/\s)]+)",
                (ROOT / name).read_text(encoding="utf-8"),
            ))
        self.assertTrue(links)
        self.assertEqual(set(links), {sha})

    def test_policy_is_only_a_trusted_reusable_call(self):
        self.assertEqual(set(self.policy["on"]), {"pull_request_target"})
        event = self.policy["on"]["pull_request_target"]
        self.assertEqual(event["branches"], ["main"])
        self.assertEqual(set(event["types"]), {
            "opened", "reopened", "synchronize", "labeled", "unlabeled", "ready_for_review",
        })
        self.assertEqual(self.policy["permissions"], {"contents": "read"})
        self.assertEqual(set(self.policy["jobs"]), {"migration-note-policy"})
        self.assertEqual(set(self.policy["jobs"]["migration-note-policy"]), {"uses"})
        self.assertNotIn("concurrency", self.policy)

    def test_draft_callee_owns_serialization_and_default_configuration(self):
        self.assertEqual(set(self.draft["on"]), {"push", "workflow_dispatch"})
        self.assertEqual(self.draft["on"]["push"]["branches"], ["main"])
        self.assertEqual(self.draft["permissions"], {
            "contents": "write", "pull-requests": "write",
        })
        self.assertEqual(set(self.draft["jobs"]), {"update_release_draft"})
        self.assertEqual(set(self.draft["jobs"]["update_release_draft"]), {"uses"})
        self.assertNotIn("concurrency", self.draft)
        for path in (
            ".github/release-drafter.yml", "towncrier.toml",
            ".github/migration-notes.md.jinja", ".github/scripts/migration_notes.py",
            ".github/scripts/migration_guides.py", ".github/scripts/update_release_draft.py",
        ):
            self.assertFalse((ROOT / path).exists(), path)

    def test_tag_workflow_serializes_whole_protected_publication(self):
        self.assertEqual(self.release["on"], {"push": {"tags": ["v*"]}})
        self.assertEqual(self.release["concurrency"], {
            "group": "release-drafter", "cancel-in-progress": "false", "queue": "max",
        })
        self.assertEqual(self.release["permissions"], {"contents": "read"})
        self.assertEqual(set(self.release["jobs"]), {"prepare", "build", "publish"})
        self.assertEqual(self.publish["environment"], "nuget.org")
        self.assertEqual(self.publish["permissions"], {
            "contents": "write", "id-token": "write",
        })
        self.assertNotIn("concurrency", self.publish)

    def test_consumer_build_is_isolated_from_release_permissions(self):
        self.assertEqual(self.prepare["permissions"], {"contents": "write"})
        self.assertEqual(len(self.prepare["steps"]), 1)
        self.assertIn("/actions/prepare-release@", self.prepare["steps"][0]["uses"])
        self.assertEqual(self.build["permissions"], {"contents": "read"})
        self.assertNotIn("environment", self.build)
        self.assertEqual(self.build["needs"], "prepare")
        self.assertEqual(self.publish["needs"], ["prepare", "build"])
        for step in self.prepare["steps"] + self.publish["steps"]:
            self.assertNotIn("actions/checkout@", step.get("uses", ""))
            self.assertNotIn("global-json-file", step.get("with", {}))
            self.assertNotRegex(step.get("run", ""), r"dotnet (restore|build|test|pack)\b")
        for name in ("Install dependencies", "Build", "Test", "Pack"):
            self.assertIn(self.step(name), self.build["steps"])

    def test_preparation_precedes_all_consumers_and_finalization_is_last(self):
        self.assertEqual(self.steps[0]["id"], "prepare")
        self.assertNotIn("if", self.steps[0])
        self.assertEqual(set(self.steps[0]), {"name", "id", "uses"})
        self.assertEqual(self.steps[-1]["name"], "Finalize GitHub Release")
        self.assertEqual(self.steps[-1]["with"], {
            "context": "${{ steps.prepare.outputs.context }}",
        })
        names = [step["name"] for step in self.steps]
        ordered = [
            "Prepare release", "Check out validated source", "Build", "Test", "Pack",
            "Validate package version", "Upload packages", "Revalidate release after approval",
            "Verify preparation matches built artifacts", "Download validated packages", "Log in to NuGet.org",
            "Publish Package", "Attach packages to GitHub Release", "Finalize GitHub Release",
        ]
        self.assertEqual(sorted(names.index(name) for name in ordered),
                         [names.index(name) for name in ordered])

    def test_published_reruns_skip_consumer_side_effects(self):
        for job in (self.build, self.publish):
            self.assertEqual(job["if"], "needs.prepare.outputs.already-published != 'true'")
            self.assertNotIn("continue-on-error", job)
        self.assertNotIn("if", self.publish["steps"][0])
        for step in self.steps:
            with self.subTest(step=step["name"]):
                self.assertNotIn("continue-on-error", step)
                if step["name"] == "Upload Test Results":
                    self.assertEqual(step["if"],
                        "always() && steps.test.outcome != 'skipped'")
                elif step in self.publish["steps"][1:]:
                    self.assertEqual(step["if"], PUBLISH_GUARD)
                else:
                    self.assertNotIn("if", step)

    def test_build_uses_prepared_source_and_version(self):
        checkout = self.step("Check out validated source")["with"]
        self.assertEqual(checkout, {
            "ref": "${{ needs.prepare.outputs.sha }}",
            "fetch-depth": "0", "persist-credentials": "false",
        })
        for name, source in (
            ("Validate package version", "needs.prepare"),
            ("Publish Package", "steps.prepare"),
        ):
            self.assertEqual(
                self.step(name)["env"]["PACKAGE_VERSION"],
                "${{ " + source + ".outputs.version }}",
            )
        self.assertEqual(self.prepare["outputs"], {
            field: "${{ steps.prepare.outputs." + field + " }}"
            for field in ("sha", "version", "context", "already-published")
        })
        self.assertEqual(self.step("Verify preparation matches built artifacts")["env"], {
            "BUILD_CONTEXT": "${{ needs.prepare.outputs.context }}",
            "PUBLISH_CONTEXT": "${{ steps.prepare.outputs.context }}",
            "ARTIFACT_ID": "${{ needs.build.outputs.artifact-id }}",
        })
        self.assertEqual(
            self.step("Attach packages to GitHub Release")["env"]["RELEASE_TAG"],
            "${{ steps.prepare.outputs.tag }}",
        )
        self.assertEqual(self.step("Log in to NuGet.org")["with"]["user"], "thalman")
        self.assertIn("--no-build", self.step("Pack")["run"])
        self.assertIn("--skip-duplicate", self.step("Publish Package")["run"])
        self.assertIn("--clobber", self.step("Attach packages to GitHub Release")["run"])
        self.assertNotIn("defaults", self.release)
        self.assertNotIn("defaults", self.publish)
        self.assertNotIn("defaults", self.prepare)
        for step in self.steps:
            if "run" in step:
                if step in self.build["steps"]:
                    self.assertEqual(step["working-directory"], "src")
                else:
                    self.assertNotIn("working-directory", step)
                self.assertNotIn("${{", step["run"])

    def test_package_and_symbol_artifacts_are_retained(self):
        upload = self.step("Upload packages")["with"]
        self.assertEqual(upload["retention-days"], "1")
        self.assertEqual(upload["if-no-files-found"], "error")
        self.assertEqual(upload["path"].splitlines(), [
            "src/package-output/*.nupkg", "src/package-output/*.snupkg",
        ])
        self.assertEqual(self.step("Upload packages")["id"], "packages")
        self.assertEqual(self.build["outputs"], {
            "artifact-id": "${{ steps.packages.outputs.artifact-id }}",
        })
        self.assertEqual(self.step("Download validated packages")["with"], {
            "artifact-ids": "${{ needs.build.outputs.artifact-id }}",
            "path": "package-output", "merge-multiple": "true",
        })

    def test_product_ci_runs_when_generated_pr_is_ready(self):
        self.assertIn("ready_for_review", workflow("ci.yml")["on"]["pull_request"]["types"])

    def test_renovate_groups_toolkit_entrypoints(self):
        config = json.loads((ROOT / ".github" / "renovate.json").read_text(encoding="utf-8"))
        group = next(rule for rule in config["packageRules"]
                     if rule["groupName"] == "release-automation")
        self.assertEqual(group["matchManagers"], ["github-actions"])
        self.assertEqual(group["matchPackageNames"], [TOOLKIT, f"{TOOLKIT}/**"])


class PackageValidationTests(unittest.TestCase):
    def bash(self):
        bash = shutil.which("bash")
        if bash is None and os.name == "nt" and (git := shutil.which("git")):
            candidate = Path(git).resolve().parents[1] / "bin" / "bash.exe"
            if candidate.is_file():
                bash = str(candidate)
        if bash is None:
            self.fail("Bash is required to test the release workflow's package validation.")
        return bash

    def test_actual_workflow_script_rejects_missing_extra_and_mismatched_artifacts(self):
        bash = self.bash()
        steps = workflow("release.yml")["jobs"]["build"]["steps"]
        script = next(step["run"] for step in steps if step["name"] == "Validate package version")
        package = "Valleysoft.DockerCredsProvider.3.0.0.nupkg"
        symbols = "Valleysoft.DockerCredsProvider.3.0.0.snupkg"
        cases = [
            ([package, symbols], True),
            ([], False),
            ([package], False),
            ([symbols], False),
            ([package, symbols, "extra.nupkg"], False),
            ([package, symbols, "extra.snupkg"], False),
            ([package.replace("3.0.0", "3.0.1"), symbols], False),
            ([package, symbols.replace("3.0.0", "3.0.1")], False),
        ]
        for files, expected_success in cases:
            with self.subTest(files=files), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / "package-output"
                output.mkdir()
                for name in files:
                    (output / name).touch()
                result = subprocess.run(
                    [bash, "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", script],
                    cwd=directory, env={**os.environ, "PACKAGE_VERSION": "3.0.0"},
                    capture_output=True, text=True, check=False,
                )
                self.assertEqual(result.returncode == 0, expected_success, result.stderr)
                if not expected_success:
                    self.assertIn("::error::", result.stdout)

    def test_publication_requires_unchanged_context_and_one_artifact_id(self):
        steps = workflow("release.yml")["jobs"]["publish"]["steps"]
        script = next(step["run"] for step in steps
                      if step["name"] == "Verify preparation matches built artifacts")
        cases = [
            ('{"sha":"prepared"}', '{"sha":"prepared"}', "123", True),
            ('{"sha":"prepared"}', '{"sha":"changed"}', "123", False),
            ("", "", "123", False),
            ("", '{"sha":"prepared"}', "123", False),
            ('{"sha":"prepared"}', '{"sha":"prepared"}', "", False),
            ('{"sha":"prepared"}', '{"sha":"prepared"}', "0", False),
            ('{"sha":"prepared"}', '{"sha":"prepared"}', "123,456", False),
        ]
        for before, after, artifact_id, expected_success in cases:
            with self.subTest(before=before, after=after, artifact_id=artifact_id):
                result = subprocess.run(
                    [self.bash(), "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", script],
                    env={**os.environ, "BUILD_CONTEXT": before, "PUBLISH_CONTEXT": after,
                         "ARTIFACT_ID": artifact_id},
                    capture_output=True, text=True, check=False,
                )
                self.assertEqual(result.returncode == 0, expected_success, result.stderr)
                if not expected_success:
                    self.assertIn("::error::", result.stdout)


if __name__ == "__main__":
    unittest.main()
