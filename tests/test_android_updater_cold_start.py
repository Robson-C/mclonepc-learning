from __future__ import annotations

import pathlib
import subprocess
import tempfile
import textwrap
import unittest


class AndroidUpdaterColdStartTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = pathlib.Path(__file__).resolve().parents[1]
        cls.source_root = (
            cls.root
            / "android"
            / "updater"
            / "src"
            / "main"
            / "java"
        )
        cls.activity_source = (
            cls.source_root
            / "io"
            / "github"
            / "robsonc"
            / "mclonepc"
            / "UpdateGateActivity.java"
        ).read_text(encoding="utf-8")

    def test_process_guard_allows_only_one_claim(self) -> None:
        guard_source = (
            self.source_root
            / "io"
            / "github"
            / "robsonc"
            / "mclonepc"
            / "ProcessUpdateCheckGuard.java"
        )
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = pathlib.Path(temporary)
            harness = (
                temporary_root
                / "io"
                / "github"
                / "robsonc"
                / "mclonepc"
                / "ProcessUpdateCheckGuardHarness.java"
            )
            harness.parent.mkdir(parents=True)
            harness.write_text(
                textwrap.dedent(
                    """\
                    package io.github.robsonc.mclonepc;

                    public final class ProcessUpdateCheckGuardHarness {
                        public static void main(String[] arguments) {
                            if (!ProcessUpdateCheckGuard.claim()) {
                                throw new AssertionError("first claim denied");
                            }
                            if (ProcessUpdateCheckGuard.claim()) {
                                throw new AssertionError("second claim allowed");
                            }
                        }
                    }
                    """
                ),
                encoding="utf-8",
            )
            classes = temporary_root / "classes"
            classes.mkdir()
            subprocess.run(
                [
                    "javac",
                    "-d",
                    str(classes),
                    str(guard_source),
                    str(harness),
                ],
                check=True,
            )
            subprocess.run(
                [
                    "java",
                    "-cp",
                    str(classes),
                    "io.github.robsonc.mclonepc.ProcessUpdateCheckGuardHarness",
                ],
                check=True,
            )

    def test_activity_claims_before_skip_or_network_check(self) -> None:
        claim = self.activity_source.index("ProcessUpdateCheckGuard.claim()")
        skip = self.activity_source.index(
            "getIntent().getBooleanExtra(EXTRA_SKIP_CHECK_ONCE"
        )
        thread = self.activity_source.index('new Thread(')
        self.assertLess(claim, skip)
        self.assertLess(skip, thread)
        self.assertIn("if (!firstGateInProcess)", self.activity_source)
        self.assertIn(
            "Update check already handled by this process.",
            self.activity_source,
        )


if __name__ == "__main__":
    unittest.main()
