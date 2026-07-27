from __future__ import annotations

import pathlib
import subprocess
import tempfile
import textwrap
import unittest


class AndroidCloudSavePerformanceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = pathlib.Path(__file__).resolve().parents[1]
        cls.source_root = (
            cls.root
            / "android"
            / "cloudsave"
            / "src"
            / "main"
            / "java"
            / "plugin"
            / "mclonecloud"
        )
        cls.client_source = (
            cls.source_root / "AndroidCloudClient.java"
        ).read_text(encoding="utf-8")

    def test_token_cache_reuses_and_expires_token(self) -> None:
        cache_source = self.source_root / "AccessTokenCache.java"
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = pathlib.Path(temporary)
            harness = (
                temporary_root
                / "plugin"
                / "mclonecloud"
                / "AccessTokenCacheHarness.java"
            )
            harness.parent.mkdir(parents=True)
            harness.write_text(
                textwrap.dedent(
                    """\
                    package plugin.mclonecloud;

                    public final class AccessTokenCacheHarness {
                        private static final class Clock
                            implements AccessTokenCache.Clock {
                            long value;

                            @Override
                            public long now() {
                                return value;
                            }
                        }

                        public static void main(String[] arguments) {
                            Clock clock = new Clock();
                            AccessTokenCache cache =
                                new AccessTokenCache(1000L, clock);
                            cache.put("token-a");
                            if (!"token-a".equals(cache.get())) {
                                throw new AssertionError("token not reused");
                            }
                            clock.value = 999L;
                            if (!"token-a".equals(cache.get())) {
                                throw new AssertionError("token expired early");
                            }
                            clock.value = 1000L;
                            if (cache.get() != null) {
                                throw new AssertionError("expired token reused");
                            }
                            cache.put("token-b");
                            cache.clear();
                            if (cache.get() != null) {
                                throw new AssertionError("clear failed");
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
                    str(cache_source),
                    str(harness),
                ],
                check=True,
            )
            subprocess.run(
                [
                    "java",
                    "-cp",
                    str(classes),
                    "plugin.mclonecloud.AccessTokenCacheHarness",
                ],
                check=True,
            )

    def test_client_reauthenticates_once_after_rejected_token(self) -> None:
        self.assertIn("getCachedAccessToken()", self.client_source)
        self.assertIn("cacheAccessToken(accessToken)", self.client_source)
        self.assertIn("!retryingAfterRejectedToken", self.client_source)
        self.assertIn("isRejectedAccessToken(exception)", self.client_source)
        self.assertIn("clearSessionCache()", self.client_source)
        self.assertIn("REMOTE_FILE_CACHE_MS", self.client_source)


if __name__ == "__main__":
    unittest.main()
