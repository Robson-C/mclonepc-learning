import pathlib
import unittest


class AndroidCloudSaveContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = pathlib.Path(__file__).resolve().parents[1]
        cls.source = (
            root
            / "android"
            / "cloudsave"
            / "src"
            / "main"
            / "java"
            / "plugin"
            / "mclonecloud"
            / "AndroidCloudClient.java"
        ).read_text(encoding="utf-8")

    def test_uses_supported_google_identity_authorization(self):
        self.assertIn("AuthorizationClient", self.source)
        self.assertIn("Identity.getAuthorizationClient", self.source)
        self.assertIn("AuthorizationRequest.builder()", self.source)
        self.assertIn(
            "https://www.googleapis.com/auth/drive.appdata",
            self.source,
        )

    def test_has_no_mobile_loopback_or_embedded_oauth_secret(self):
        self.assertNotIn("127.0.0.1", self.source)
        self.assertNotIn("localhost", self.source.lower())
        self.assertNotIn("client_secret", self.source.lower())
        self.assertNotIn("refresh_token", self.source.lower())

    def test_preserves_shared_drive_contract(self):
        self.assertIn("mclonepc-game-cloud-v1.json", self.source)
        for action in (
            "connect",
            "verify",
            "status",
            "upload",
            "download",
            "logout",
        ):
            self.assertIn(f'"{action}"', self.source)
        for key in (
            "schemaVersion",
            "bundleSha256",
            "saveFingerprint",
            "revision",
            "createdUtc",
        ):
            self.assertIn(f'"{key}"', self.source)


if __name__ == "__main__":
    unittest.main()
