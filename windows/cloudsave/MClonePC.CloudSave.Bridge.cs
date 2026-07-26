using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MClonePC.CloudSave
{
    internal static class CloudSaveBridge
    {
        private const int AuthenticationErrorStatus = 2001;
        private const int MaximumPayloadBytes = 64 * 1024 * 1024;

        public static int Run(
            string installRoot,
            CloudSaveConfig config,
            string[] args
        )
        {
            string resultPath = GetOption(args, "--result");
            try
            {
                if (args.Length == 0)
                {
                    throw new InvalidDataException(
                        "Ação da ponte de nuvem ausente."
                    );
                }
                EnsureAllowedJsonPath(config, resultPath);
                Dictionary<string, object> result = ExecuteAsync(
                    config,
                    args[0],
                    args
                ).GetAwaiter().GetResult();
                WriteResponse(resultPath, true, result, null, 0);
                return 0;
            }
            catch (Exception exception)
            {
                int status = IsAuthenticationError(exception)
                    ? AuthenticationErrorStatus
                    : 0;
                if (!String.IsNullOrWhiteSpace(resultPath))
                {
                    try
                    {
                        EnsureAllowedJsonPath(config, resultPath);
                        WriteResponse(
                            resultPath,
                            false,
                            null,
                            exception.Message,
                            status
                        );
                    }
                    catch
                    {
                    }
                }
                return status == AuthenticationErrorStatus ? 3 : 2;
            }
        }

        public static int RunServer(
            string installRoot,
            CloudSaveConfig config,
            string[] args
        )
        {
            string readyPath = GetOption(args, "--ready");
            EnsureReadyPath(installRoot, readyPath);
            bool ownsMutex;
            using (Mutex mutex = new Mutex(
                true,
                @"Local\MClonePC.CloudSave.Bridge.v1",
                out ownsMutex
            ))
            {
                if (!ownsMutex)
                {
                    return 0;
                }
                try
                {
                    AtomicWrite(
                        readyPath,
                        Encoding.ASCII.GetBytes("ready")
                    );
                    DateTime startupDeadline =
                        DateTime.UtcNow.AddSeconds(30);
                    string gameExecutable = Path.Combine(
                        installRoot,
                        "game",
                        "mclonepc.exe"
                    );
                    DateTime? gameMissingSince = null;
                    bool gameWasObserved = false;
                    while (true)
                    {
                        ProcessPendingRequests(installRoot, config);
                        bool gameRunning =
                            CloudSaveRuntime.IsGameRunningAtPath(
                                gameExecutable
                            );
                        if (gameRunning)
                        {
                            gameWasObserved = true;
                            gameMissingSince = null;
                        }
                        else if (gameWasObserved)
                        {
                            if (!gameMissingSince.HasValue)
                            {
                                gameMissingSince = DateTime.UtcNow;
                            }
                            else if (
                                DateTime.UtcNow - gameMissingSince.Value >=
                                    TimeSpan.FromSeconds(2)
                            )
                            {
                                break;
                            }
                        }
                        else if (DateTime.UtcNow >= startupDeadline)
                        {
                            break;
                        }
                        Thread.Sleep(100);
                    }
                    return 0;
                }
                finally
                {
                    if (File.Exists(readyPath))
                    {
                        File.Delete(readyPath);
                    }
                    mutex.ReleaseMutex();
                }
            }
        }

        private static void ProcessPendingRequests(
            string installRoot,
            CloudSaveConfig config
        )
        {
            string directory = config.GetExpandedSaveDirectory();
            Directory.CreateDirectory(directory);
            string[] requests = Directory.GetFiles(
                directory,
                ".mclonepc-cloud-request-*.json",
                SearchOption.TopDirectoryOnly
            );
            Array.Sort(requests, StringComparer.OrdinalIgnoreCase);
            foreach (string requestPath in requests)
            {
                string requestId = GetRequestId(requestPath);
                if (requestId == null)
                {
                    continue;
                }
                string claimedPath = requestPath + ".processing";
                try
                {
                    File.Move(requestPath, claimedPath);
                }
                catch (IOException)
                {
                    continue;
                }

                string resultPath = Path.Combine(
                    directory,
                    ".mclonepc-cloud-result-" + requestId + ".json"
                );
                string payloadPath = Path.Combine(
                    directory,
                    ".mclonepc-cloud-payload-" + requestId + ".json"
                );
                try
                {
                    FileInfo requestFile = new FileInfo(claimedPath);
                    if (
                        requestFile.Length <= 0 ||
                        requestFile.Length > 65536
                    )
                    {
                        throw new InvalidDataException(
                            "Tamanho inválido da solicitação de nuvem."
                        );
                    }
                    JavaScriptSerializer serializer =
                        new JavaScriptSerializer();
                    Dictionary<string, object> request = serializer.Deserialize<
                        Dictionary<string, object>
                    >(File.ReadAllText(claimedPath, Encoding.UTF8));
                    object schema;
                    object actionValue;
                    if (
                        request == null ||
                        !request.TryGetValue(
                            "schema_version",
                            out schema
                        ) ||
                        Convert.ToInt32(schema) != 1 ||
                        !request.TryGetValue("action", out actionValue)
                    )
                    {
                        throw new InvalidDataException(
                            "Solicitação de nuvem inválida."
                        );
                    }
                    string action = Convert.ToString(actionValue);
                    if (!IsAllowedServerAction(action))
                    {
                        throw new InvalidDataException(
                            "Ação de nuvem inválida."
                        );
                    }
                    List<string> bridgeArgs = new List<string>();
                    bridgeArgs.Add(action);
                    bridgeArgs.Add("--result");
                    bridgeArgs.Add(resultPath);
                    if (
                        String.Equals(
                            action,
                            "upload",
                            StringComparison.Ordinal
                        ) ||
                        String.Equals(
                            action,
                            "download",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        bridgeArgs.Add("--payload");
                        bridgeArgs.Add(payloadPath);
                    }
                    Run(installRoot, config, bridgeArgs.ToArray());
                }
                catch (Exception exception)
                {
                    WriteResponse(
                        resultPath,
                        false,
                        null,
                        exception.Message,
                        0
                    );
                }
                finally
                {
                    if (File.Exists(claimedPath))
                    {
                        File.Delete(claimedPath);
                    }
                }
            }
        }

        private static bool IsAllowedServerAction(string action)
        {
            return
                String.Equals(action, "connect", StringComparison.Ordinal) ||
                String.Equals(action, "logout", StringComparison.Ordinal) ||
                String.Equals(action, "verify", StringComparison.Ordinal) ||
                String.Equals(action, "status", StringComparison.Ordinal) ||
                String.Equals(action, "upload", StringComparison.Ordinal) ||
                String.Equals(action, "download", StringComparison.Ordinal);
        }

        private static string GetRequestId(string requestPath)
        {
            const string prefix = ".mclonepc-cloud-request-";
            const string suffix = ".json";
            string name = Path.GetFileName(requestPath);
            if (
                !name.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                !name.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return null;
            }
            string requestId = name.Substring(
                prefix.Length,
                name.Length - prefix.Length - suffix.Length
            );
            if (requestId.Length < 3 || requestId.Length > 80)
            {
                return null;
            }
            foreach (char character in requestId)
            {
                if (
                    !Char.IsLetterOrDigit(character) &&
                    character != '-'
                )
                {
                    return null;
                }
            }
            return requestId;
        }

        private static void EnsureReadyPath(
            string installRoot,
            string readyPath
        )
        {
            if (String.IsNullOrWhiteSpace(readyPath))
            {
                throw new InvalidDataException(
                    "Caminho de prontidão da ponte ausente."
                );
            }
            string root = Path.GetFullPath(installRoot).TrimEnd('\\');
            string stateRoot = Path.Combine(root, "state");
            string fullReadyPath = Path.GetFullPath(readyPath);
            if (
                !fullReadyPath.StartsWith(
                    stateRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                !String.Equals(
                    Path.GetFileName(fullReadyPath),
                    "cloud-bridge.ready",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidDataException(
                    "Caminho de prontidão da ponte inválido."
                );
            }
        }

        private static async Task<Dictionary<string, object>> ExecuteAsync(
            CloudSaveConfig config,
            string action,
            string[] args
        )
        {
            string stateDirectory = CloudSaveRuntime.GetStateDirectory();
            OAuthTokenStore tokenStore = new OAuthTokenStore(stateDirectory);
            using (GoogleOAuthClient oauth =
                new GoogleOAuthClient(config, tokenStore))
            using (GoogleDriveAppDataClient drive =
                new GoogleDriveAppDataClient(oauth))
            using (CancellationTokenSource cancellation =
                new CancellationTokenSource(TimeSpan.FromMinutes(15)))
            {
                if (String.Equals(action, "connect", StringComparison.Ordinal))
                {
                    await oauth.ConnectAsync(cancellation.Token);
                    return await GetStatusAsync(
                        config,
                        drive,
                        tokenStore,
                        cancellation.Token
                    );
                }
                if (String.Equals(action, "logout", StringComparison.Ordinal))
                {
                    oauth.Disconnect();
                    return new Dictionary<string, object>
                    {
                        { "connected", false },
                        { "noBackup", true }
                    };
                }

                EnsureConnected(tokenStore);
                if (
                    String.Equals(action, "verify", StringComparison.Ordinal) ||
                    String.Equals(action, "status", StringComparison.Ordinal)
                )
                {
                    return await GetStatusAsync(
                        config,
                        drive,
                        tokenStore,
                        cancellation.Token
                    );
                }
                if (String.Equals(action, "upload", StringComparison.Ordinal))
                {
                    string payloadPath = GetOption(args, "--payload");
                    EnsureAllowedJsonPath(config, payloadPath);
                    byte[] payload = ReadAndValidatePayload(payloadPath);
                    RemoteSaveFile remote = await drive.FindAsync(
                        config.game_remote_file_name,
                        cancellation.Token
                    );
                    int revision = remote == null
                        ? 1
                        : Math.Max(1, remote.Revision + 1);
                    string hash = ComputeSha256(payload);
                    RemoteSaveFile uploaded = await drive.UploadAsync(
                        config.game_remote_file_name,
                        remote == null ? null : remote.Id,
                        payload,
                        hash,
                        hash,
                        revision,
                        cancellation.Token,
                        "application/json"
                    );
                    return BuildRemoteResult(uploaded);
                }
                if (String.Equals(action, "download", StringComparison.Ordinal))
                {
                    string payloadPath = GetOption(args, "--payload");
                    EnsureAllowedJsonPath(config, payloadPath);
                    RemoteSaveFile remote = await drive.FindAsync(
                        config.game_remote_file_name,
                        cancellation.Token
                    );
                    if (remote == null)
                    {
                        return new Dictionary<string, object>
                        {
                            { "connected", true },
                            { "noBackup", true }
                        };
                    }
                    byte[] payload = await drive.DownloadAsync(
                        remote.Id,
                        cancellation.Token
                    );
                    ValidatePayload(payload);
                    AtomicWrite(payloadPath, payload);
                    return BuildRemoteResult(remote);
                }
                throw new InvalidDataException(
                    "Ação desconhecida da ponte de nuvem: " + action
                );
            }
        }

        private static async Task<Dictionary<string, object>> GetStatusAsync(
            CloudSaveConfig config,
            GoogleDriveAppDataClient drive,
            OAuthTokenStore tokenStore,
            CancellationToken cancellationToken
        )
        {
            EnsureConnected(tokenStore);
            RemoteSaveFile remote = await drive.FindAsync(
                config.game_remote_file_name,
                cancellationToken
            );
            if (remote == null)
            {
                return new Dictionary<string, object>
                {
                    { "connected", true },
                    { "noBackup", true }
                };
            }
            return BuildRemoteResult(remote);
        }

        private static Dictionary<string, object> BuildRemoteResult(
            RemoteSaveFile remote
        )
        {
            DateTime time = remote.ModifiedTimeUtc.HasValue
                ? remote.ModifiedTimeUtc.Value
                : DateTime.UtcNow;
            return new Dictionary<string, object>
            {
                { "connected", true },
                { "noBackup", false },
                { "lastSaveTime", ToUnixSeconds(time) },
                { "revision", Math.Max(1, remote.Revision) }
            };
        }

        private static void EnsureConnected(OAuthTokenStore tokenStore)
        {
            if (!tokenStore.Exists())
            {
                throw new InvalidOperationException(
                    "Conecte uma conta Google antes de acessar a nuvem."
                );
            }
        }

        private static string GetOption(string[] args, string option)
        {
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (String.Equals(args[index], option, StringComparison.Ordinal))
                {
                    return args[index + 1];
                }
            }
            return null;
        }

        private static void EnsureAllowedJsonPath(
            CloudSaveConfig config,
            string path
        )
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException(
                    "Caminho de comunicação com o jogo ausente."
                );
            }
            string fullPath = Path.GetFullPath(path);
            string saveRoot = config.GetExpandedSaveDirectory();
            if (
                !fullPath.StartsWith(
                    saveRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                !String.Equals(
                    Path.GetExtension(fullPath),
                    ".json",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidDataException(
                    "A ponte recusou um caminho fora da pasta de save."
                );
            }
        }

        private static byte[] ReadAndValidatePayload(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "Payload do jogo não encontrado.",
                    path
                );
            }
            if (file.Length <= 0 || file.Length > MaximumPayloadBytes)
            {
                throw new InvalidDataException(
                    "Tamanho inválido do payload do jogo."
                );
            }
            byte[] payload = File.ReadAllBytes(path);
            ValidatePayload(payload);
            return payload;
        }

        private static void ValidatePayload(byte[] payload)
        {
            if (
                payload == null ||
                payload.Length <= 0 ||
                payload.Length > MaximumPayloadBytes
            )
            {
                throw new InvalidDataException(
                    "Tamanho inválido do payload do jogo."
                );
            }
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaximumPayloadBytes;
            Dictionary<string, object> root =
                serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(payload)
                );
            object schema;
            if (
                root == null ||
                !root.TryGetValue("schema_version", out schema) ||
                Convert.ToInt32(schema) != 1 ||
                !root.ContainsKey("gameData") ||
                !root.ContainsKey("info")
            )
            {
                throw new InvalidDataException(
                    "Payload interno do jogo inválido."
                );
            }
        }

        private static void WriteResponse(
            string resultPath,
            bool ok,
            Dictionary<string, object> result,
            string error,
            int status
        )
        {
            Dictionary<string, object> response =
                new Dictionary<string, object>();
            response["schema_version"] = 1;
            response["ok"] = ok;
            if (ok)
            {
                response["result"] = result;
            }
            else
            {
                response["error"] = error ?? "Falha desconhecida.";
                response["status"] = status;
            }
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaximumPayloadBytes;
            AtomicWrite(
                resultPath,
                Encoding.UTF8.GetBytes(serializer.Serialize(response))
            );
        }

        private static void AtomicWrite(string path, byte[] content)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = path + "." +
                Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, content);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static string ComputeSha256(byte[] content)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(content);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    text.Append(value.ToString("x2"));
                }
                return text.ToString();
            }
        }

        private static long ToUnixSeconds(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc
                ? value
                : value.ToUniversalTime();
            return Convert.ToInt64(
                Math.Floor(
                    (utc - new DateTime(
                        1970,
                        1,
                        1,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc
                    )).TotalSeconds
                )
            );
        }

        private static bool IsAuthenticationError(Exception exception)
        {
            string message = exception.Message ?? "";
            return
                message.IndexOf(
                    "Conecte uma conta Google",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0 ||
                message.IndexOf(
                    "invalid_grant",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0;
        }
    }
}
