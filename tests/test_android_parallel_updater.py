from __future__ import annotations

import pathlib
import subprocess
import tempfile
import textwrap
import unittest


class AndroidParallelUpdaterTests(unittest.TestCase):
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
            / "io"
            / "github"
            / "robsonc"
            / "mclonepc"
        )
        cls.activity_source = (
            cls.source_root / "UpdateGateActivity.java"
        ).read_text(encoding="utf-8")

    def test_ranges_cover_file_and_parts_assemble_in_order(self) -> None:
        downloader_source = (
            self.source_root / "ParallelApkDownloader.java"
        )
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = pathlib.Path(temporary)
            harness = (
                temporary_root
                / "io"
                / "github"
                / "robsonc"
                / "mclonepc"
                / "ParallelApkDownloaderHarness.java"
            )
            harness.parent.mkdir(parents=True)
            harness.write_text(
                textwrap.dedent(
                    """\
                    package io.github.robsonc.mclonepc;

                    import java.io.File;
                    import java.io.FileOutputStream;
                    import java.nio.file.Files;
                    import java.util.ArrayList;
                    import java.util.List;

                    public final class ParallelApkDownloaderHarness {
                        public static void main(String[] arguments)
                            throws Exception {
                            List<ParallelApkDownloader.RangePart> ranges =
                                ParallelApkDownloader.createRanges(10L, 4);
                            long next = 0L;
                            for (
                                ParallelApkDownloader.RangePart range : ranges
                            ) {
                                if (range.start != next) {
                                    throw new AssertionError("range gap");
                                }
                                next = range.end + 1L;
                            }
                            if (next != 10L) {
                                throw new AssertionError("range tail");
                            }
                            if (!ParallelApkDownloader.contentRangeMatches(
                                "bytes 0-0/10",
                                0L,
                                0L,
                                10L
                            )) {
                                throw new AssertionError("valid header denied");
                            }

                            File root = Files.createTempDirectory(
                                "mclone-range-test"
                            ).toFile();
                            List<File> parts = new ArrayList<File>();
                            byte[][] values = {
                                {0, 1},
                                {2, 3},
                                {4, 5},
                                {6}
                            };
                            for (int index = 0; index < values.length; index++) {
                                File part = new File(root, "part" + index);
                                try (
                                    FileOutputStream output =
                                        new FileOutputStream(part)
                                ) {
                                    output.write(values[index]);
                                }
                                parts.add(part);
                            }
                            File assembled = new File(root, "assembled.apk");
                            ParallelApkDownloader.assemble(
                                parts,
                                7L,
                                assembled
                            );
                            byte[] actual = Files.readAllBytes(
                                assembled.toPath()
                            );
                            for (int index = 0; index < actual.length; index++) {
                                if (actual[index] != (byte) index) {
                                    throw new AssertionError(
                                        "assembly order"
                                    );
                                }
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
                    str(downloader_source),
                    str(harness),
                ],
                check=True,
            )
            subprocess.run(
                [
                    "java",
                    "-cp",
                    str(classes),
                    (
                        "io.github.robsonc.mclonepc."
                        "ParallelApkDownloaderHarness"
                    ),
                ],
                check=True,
            )

    def test_activity_keeps_integrity_validation_and_serial_fallback(self) -> None:
        self.assertIn("ParallelApkDownloader.download(", self.activity_source)
        self.assertIn("downloadApkSingleStream(", self.activity_source)
        self.assertIn("validateDownloadedBytes(", self.activity_source)
        self.assertIn("APK SHA-256 mismatch.", self.activity_source)
        self.assertIn("validateDownloadedApk(artifact, temporary)", self.activity_source)


if __name__ == "__main__":
    unittest.main()
