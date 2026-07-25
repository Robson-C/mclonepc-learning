using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MClonePC.CloudSave;

internal static class CloudSaveSelfTest
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Uso: cloud-save-selftest <temp-root>");
            return 2;
        }

        string root = Path.GetFullPath(args[0]);
        string source = Path.Combine(root, "source-save");
        string target = Path.Combine(root, "target-save");
        string work = Path.Combine(root, "work");
        string backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(
            Path.Combine(source, "PlayerData.txt"),
            "save-original",
            Encoding.UTF8
        );
        File.WriteAllBytes(
            Path.Combine(source, "nested", "UserInfo.usr"),
            Enumerable.Range(0, 256).Select(value => (byte)value).ToArray()
        );

        string configPath = Path.Combine(root, "cloud-save.json");
        File.WriteAllText(
            configPath,
            "{\"schema_version\":1," +
            "\"google_client_id\":\"123-test.apps.googleusercontent.com\"," +
            "\"google_client_secret\":\"test-secret\"," +
            "\"google_scope\":\"https://www.googleapis.com/auth/" +
            "drive.appdata\"," +
            "\"remote_file_name\":\"mclonepc-save-v1.zip\"," +
            "\"save_directory\":\"%APPDATA%\\\\Robson\\\\MClonePC" +
            "\\\\Documents\"}",
            new UTF8Encoding(false)
        );
        CloudSaveConfig config = CloudSaveConfig.Load(configPath);
        if (config.remote_file_name != "mclonepc-save-v1.zip")
        {
            throw new InvalidOperationException(
                "A configuração válida não foi carregada."
            );
        }
        string oauthError = GoogleOAuthClient.SafeGoogleError(
            "{\"error\":\"invalid_request\"," +
            "\"error_description\":\"Missing required parameter\"}"
        );
        if (
            oauthError !=
                "invalid_request: Missing required parameter"
        )
        {
            throw new InvalidOperationException(
                "A descrição detalhada do erro OAuth foi descartada."
            );
        }

        SaveBundleService bundles = new SaveBundleService(
            "0123456789abcdef0123456789abcdef"
        );
        CreatedSaveBundle created = bundles.Create(source, work, 1);
        if (
            created.Revision != 1 ||
            created.Size <= 0 ||
            created.BundleSha256.Length != 64 ||
            created.SaveFingerprint.Length != 64
        )
        {
            throw new InvalidOperationException(
                "Metadados do pacote criado são inválidos."
            );
        }

        ValidatedSaveBundle validated = bundles.ValidateAndExtract(
            created.FilePath,
            work,
            created.BundleSha256
        );
        if (
            validated.Manifest.files.Count != 2 ||
            validated.Manifest.save_fingerprint !=
                created.SaveFingerprint
        )
        {
            throw new InvalidOperationException(
                "Manifesto validado diverge do pacote criado."
            );
        }

        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, "old-save.txt"),
            "preservar em backup",
            Encoding.UTF8
        );
        RestoreService restore = new RestoreService();
        string backup = restore.Restore(
            validated,
            target,
            backups
        );
        if (
            String.IsNullOrWhiteSpace(backup) ||
            !File.Exists(Path.Combine(backup, "old-save.txt")) ||
            File.ReadAllText(
                Path.Combine(target, "PlayerData.txt"),
                Encoding.UTF8
            ) != "save-original"
        )
        {
            throw new InvalidOperationException(
                "Restauração ou backup local falhou."
            );
        }

        for (int index = 0; index < 4; index++)
        {
            string extra = Path.Combine(
                backups,
                "2000010" + index + "-000000-" + index
            );
            Directory.CreateDirectory(extra);
        }
        ValidatedSaveBundle second = bundles.ValidateAndExtract(
            created.FilePath,
            work,
            created.BundleSha256
        );
        restore.Restore(second, target, backups);
        if (Directory.GetDirectories(backups).Length != 3)
        {
            throw new InvalidOperationException(
                "A retenção não manteve exatamente três backups."
            );
        }

        string tampered = Path.Combine(root, "tampered.zip");
        File.Copy(created.FilePath, tampered);
        using (FileStream stream = new FileStream(
            tampered,
            FileMode.Append,
            FileAccess.Write
        ))
        {
            stream.WriteByte(1);
        }
        ExpectInvalidData(
            delegate
            {
                bundles.ValidateAndExtract(
                    tampered,
                    work,
                    created.BundleSha256
                );
            },
            "Pacote adulterado aceito."
        );

        string traversal = Path.Combine(root, "traversal.zip");
        File.Copy(created.FilePath, traversal);
        using (ZipArchive archive = ZipFile.Open(
            traversal,
            ZipArchiveMode.Update
        ))
        {
            ZipArchiveEntry entry = archive.CreateEntry("../escape.txt");
            using (StreamWriter writer = new StreamWriter(entry.Open()))
            {
                writer.Write("não deve sair");
            }
        }
        ExpectInvalidData(
            delegate
            {
                bundles.ValidateAndExtract(traversal, work, null);
            },
            "Pacote com travessia de diretório aceito."
        );

        Console.WriteLine("WINDOWS_CLOUDSAVE_INTEGRATION_OK");
        return 0;
    }

    private static void ExpectInvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
