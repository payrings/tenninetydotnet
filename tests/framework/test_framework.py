import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
STARTER = ROOT / "starter-kit"


def run(command, *, cwd, env=None):
    return subprocess.run(
        command,
        cwd=cwd,
        env={**os.environ, **(env or {})},
        text=True,
        capture_output=True,
    )


class RuntimeTests(unittest.TestCase):
    def test_runtime_directory_is_external_and_stable(self):
        with tempfile.TemporaryDirectory() as temporary:
            temporary = Path(temporary)
            workspace = temporary / "workspace"
            state = temporary / "state"
            workspace.mkdir()
            command = (
                f'source "{STARTER / "scripts/runtime.sh"}"; '
                f'tenninety_init_runtime "{workspace}"; printf "%s" "$TENNINETY_RUNTIME_DIR"'
            )
            first = run(["bash", "-c", command], cwd=workspace,
                        env={"TENNINETY_STATE_HOME": str(state)})
            second = run(["bash", "-c", command], cwd=workspace,
                         env={"TENNINETY_STATE_HOME": str(state)})
            self.assertEqual(first.returncode, 0, first.stderr)
            self.assertEqual(first.stdout, second.stdout)
            self.assertTrue(Path(first.stdout).is_relative_to(state))
            self.assertFalse(Path(first.stdout).is_relative_to(workspace))

    def test_runtime_directory_inside_workspace_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            unsafe = workspace / "runtime"
            command = (
                f'source "{STARTER / "scripts/runtime.sh"}"; '
                f'tenninety_init_runtime "{workspace}"'
            )
            result = run(["bash", "-c", command], cwd=workspace,
                         env={"TENNINETY_RUNTIME_DIR": str(unsafe)})
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("must be outside", result.stderr)


class QueueTests(unittest.TestCase):
    def test_approved_module_cannot_be_requeued(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            queue = workspace / "REVIEW_QUEUE.md"
            queue.write_text(
                "| Module | Status | Times rejected |\n"
                "|---|---|---:|\n"
                "| demo | approved | 0 |\n"
            )
            result = run(
                ["bash", str(STARTER / "scripts/queue_for_review.sh"), "demo"],
                cwd=workspace,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("cannot be reopened", result.stderr)
            self.assertIn("| demo | approved | 0 |", queue.read_text())

    def test_needs_fixes_can_return_to_review(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            queue = workspace / "REVIEW_QUEUE.md"
            queue.write_text(
                "| Module | Status | Times rejected |\n"
                "|---|---|---:|\n"
                "| demo | needs-fixes | 2 |\n"
            )
            result = run(
                ["bash", str(STARTER / "scripts/queue_for_review.sh"), "demo"],
                cwd=workspace,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("| demo | ready-for-review | 2 |", queue.read_text())


class StaticInvariantTests(unittest.TestCase):
    def test_sdk_pin_matches_test_image(self):
        sdk = json.loads((STARTER / "global.json").read_text())["sdk"]["version"]
        dockerfile = (STARTER / "Dockerfile.test").read_text()
        self.assertEqual(sdk, "10.0.100")
        self.assertIn(f"mcr.microsoft.com/dotnet/sdk:{sdk}", dockerfile)

    def test_semantic_checker_uses_a_clean_dedicated_host(self):
        workflow = (ROOT / ".github/workflows/framework-regression.yml").read_text()
        pre_commit = (STARTER / ".pre-commit-config.yaml").read_text()
        project = (
            STARTER / "scripts/signature-checker/Tenninety.SignatureChecker.csproj"
        ).read_text()
        checker_build = (
            STARTER / "scripts/signature-checker/Directory.Build.props"
        ).read_text()
        checker_packages = (
            STARTER / "scripts/signature-checker/Directory.Packages.props"
        ).read_text()
        checker_targets = (
            STARTER / "scripts/signature-checker/Directory.Build.targets"
        ).read_text()
        wrapper = (STARTER / "scripts/check_signatures.sh").read_text()
        semantic_test = (ROOT / "tests/framework/test_semantic_signatures.sh").read_text()
        self.assertNotIn("dotnet-script", workflow)
        checkout = (
            "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"
        )
        self.assertEqual(workflow.count(checkout), 2)
        self.assertIn("scripts/check_signatures.sh --staged", pre_commit)
        for package in (
            "Microsoft.Build",
            "Microsoft.Build.Framework",
            "Microsoft.Build.Tasks.Core",
            "Microsoft.Build.Utilities.Core",
        ):
            self.assertIn(
                f'Include="{package}" Version="17.11.48" ExcludeAssets="runtime"',
                project,
            )
        self.assertIn("RejectBundledMSBuildRuntime", project)
        self.assertIn(
            'Include="System.Security.Cryptography.Xml" Version="10.0.10"',
            project,
        )
        self.assertIn("<TargetFramework>net10.0</TargetFramework>", checker_build)
        self.assertIn(
            "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>",
            checker_packages,
        )
        self.assertIn("<NuGetAuditLevel>moderate</NuGetAuditLevel>", checker_packages)
        self.assertIn("<Project>", checker_targets)
        self.assertIn("dotnet build", wrapper)
        self.assertIn("--no-restore", wrapper)
        self.assertIn("-noAutoResponse", wrapper)
        self.assertIn('exec dotnet "$CHECKER_DLL"', wrapper)
        self.assertNotIn("-noAutoResponse >/dev/null", semantic_test)

    def test_test_containers_never_mount_real_workspace(self):
        fast = (STARTER / "scripts/run_tests_with_cascade_check.sh").read_text()
        integration = (STARTER / "scripts/run_integration_tests.sh").read_text()
        for script in (fast, integration):
            self.assertNotIn('-v "$PWD":/workspace', script)
            self.assertIn("tenninety_create_snapshot", script)
            self.assertIn("tenninety_clone_seed", script)
            self.assertIn("tenninety_run_project", script)

    def test_implicit_build_inputs_are_classified(self):
        dev = (STARTER / "scripts/dev.sh").read_text().lower()
        sandbox = (STARTER / "scripts/test_sandbox.sh").read_text().lower()
        for pattern in ("*.rsp", "*.user", ".editorconfig", "nuget.config"):
            self.assertIn(pattern, dev)
            self.assertIn(pattern, sandbox)

    def test_frontier_fix_is_displayed(self):
        dev = (STARTER / "scripts/dev.sh").read_text()
        self.assertIn('cat -- "$fix_file"', dev)
        self.assertIn('$TENNINETY_RUNTIME_DIR/$module_id/frontier-fix-', dev)

    def test_module_id_is_validated_before_read_only_path_construction(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = run(
                [
                    "bash",
                    str(STARTER / "scripts/dev.sh"),
                    "show-frontier-fix",
                    "../../escape",
                ],
                cwd=STARTER,
                env={"TENNINETY_STATE_HOME": temporary},
            )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("invalid Module ID", result.stderr)

    def test_build_control_mounts_are_not_duplicated(self):
        dev = (STARTER / "scripts/dev.sh").read_text()
        self.assertNotIn(
            'RO_MOUNTS+=(-v "$WORKSPACE/Directory.Build.props:', dev
        )
        self.assertNotIn(
            'RO_MOUNTS+=(-v "$WORKSPACE/Directory.Build.targets:', dev
        )

    def test_scope_diff_is_rename_aware_and_empty_work_fails(self):
        dev = (STARTER / "scripts/dev.sh").read_text()
        self.assertIn("--name-status -z", dev)
        self.assertIn("printf '%s\\n%s\\n' \"$first\" \"$second\"", dev)
        self.assertIn("has no implementation changes", dev)

    def test_escalation_does_not_read_workspace_env(self):
        escalation = (STARTER / "scripts/escalate.py").read_text()
        self.assertNotIn('candidates.append(Path(".env"))', escalation)
        self.assertIn('choice.finish_reason != "stop"', escalation)

    def test_raw_sql_hook_reads_the_index(self):
        hook = (STARTER / ".pre-commit-config.yaml").read_text()
        checker = (STARTER / "scripts/check_no_raw_sql.sh").read_text()
        self.assertIn("scripts/check_no_raw_sql.sh", hook)
        self.assertIn('git -C "$WORKSPACE" show ":$path"', checker)


if __name__ == "__main__":
    unittest.main()
