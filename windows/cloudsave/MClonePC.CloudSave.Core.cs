using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MClonePC.CloudSave
{
    internal sealed class CloudSaveConfig
    {
        public int schema_version { get; set; }
        public string google_client_id { get; set; }
        public string google_scope { get; set; }
        public string remote_file_name { get; set; }
        public string save_directory { get; set; }

        public static CloudSaveConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Configuração de save em nuvem ausente.",
                    path
                );
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            CloudSaveConfig config = serializer.Deserialize<CloudSaveConfig>(
                File.ReadAllText(path, Encoding.UTF8)
            );
            if (config == null || config.schema_version != 1)
            {
                throw new InvalidDataException(
                    "Versão inválida da configuração de save em nuvem."
                );
            }
            if (
                String.IsNullOrWhiteSpace(config.google_client_id) ||
                !config.google_client_id.EndsWith(
                    ".apps.googleusercontent.com",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidDataException(
                    "O Google OAuth Client ID está ausente ou inválido."
                );
            }
            if (
                !String.Equals(
                    config.google_scope,
                    "https://www.googleapis.com/auth/drive.appdata",
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    "A configuração deve usar somente o escopo drive.appdata."
                );
            }
            if (
                String.IsNullOrWhiteSpace(config.remote_file_name) ||
                Path.GetFileName(config.remote_file_name) !=
                    config.remote_file_name
            )
            {
                throw new InvalidDataException(
                    "Nome remoto inválido para o pacote de save."
                );
            }
            if (String.IsNullOrWhiteSpace(config.save_directory))
            {
                throw new InvalidDataException(
                    "Diretório local do save não configurado."
                );
            }
            return config;
        }

        public string GetExpandedSaveDirectory()
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(save_directory)
            ).TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    internal sealed class StoredOAuthToken
    {
        public int schema_version { get; set; }
        public string refresh_token { get; set; }
        public string saved_at_utc { get; set; }
    }

    internal sealed class OAuthAccessToken
    {
        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public DateTime ExpiresAtUtc { get; private set; }

        public OAuthAccessToken(
            string accessToken,
            string refreshToken,
            DateTime expiresAtUtc
        )
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            ExpiresAtUtc = expiresAtUtc;
        }

        public bool IsUsable()
        {
            return
                !String.IsNullOrWhiteSpace(AccessToken) &&
                DateTime.UtcNow.AddMinutes(1) < ExpiresAtUtc;
        }
    }

    internal sealed class OAuthTokenStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "MClonePC.CloudSave.Token.v1"
        );

        private readonly string tokenPath;

        public OAuthTokenStore(string stateDirectory)
        {
            tokenPath = Path.Combine(stateDirectory, "google-oauth.dpapi");
        }

        public bool Exists()
        {
            return File.Exists(tokenPath);
        }

        public string LoadRefreshToken()
        {
            if (!File.Exists(tokenPath))
            {
                return null;
            }

            byte[] encrypted = File.ReadAllBytes(tokenPath);
            byte[] clear = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser
            );
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                StoredOAuthToken stored = serializer.Deserialize<
                    StoredOAuthToken
                >(Encoding.UTF8.GetString(clear));
                if (
                    stored == null ||
                    stored.schema_version != 1 ||
                    String.IsNullOrWhiteSpace(stored.refresh_token)
                )
                {
                    throw new InvalidDataException(
                        "Token OAuth local inválido."
                    );
                }
                return stored.refresh_token;
            }
            finally
            {
                Array.Clear(clear, 0, clear.Length);
            }
        }

        public void SaveRefreshToken(string refreshToken)
        {
            if (String.IsNullOrWhiteSpace(refreshToken))
            {
                throw new ArgumentException("Refresh token vazio.");
            }

            string directory = Path.GetDirectoryName(tokenPath);
            Directory.CreateDirectory(directory);

            StoredOAuthToken stored = new StoredOAuthToken();
            stored.schema_version = 1;
            stored.refresh_token = refreshToken;
            stored.saved_at_utc = DateTime.UtcNow.ToString("o");

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            byte[] clear = Encoding.UTF8.GetBytes(
                serializer.Serialize(stored)
            );
            try
            {
                byte[] encrypted = ProtectedData.Protect(
                    clear,
                    Entropy,
                    DataProtectionScope.CurrentUser
                );
                string temporary = tokenPath + "." +
                    Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(tokenPath))
                {
                    File.Replace(temporary, tokenPath, null);
                }
                else
                {
                    File.Move(temporary, tokenPath);
                }
            }
            finally
            {
                Array.Clear(clear, 0, clear.Length);
            }
        }

        public void Delete()
        {
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
    }

    internal sealed class GoogleOAuthClient : IDisposable
    {
        private const string AuthorizationEndpoint =
            "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenEndpoint =
            "https://oauth2.googleapis.com/token";

        private readonly CloudSaveConfig config;
        private readonly OAuthTokenStore tokenStore;
        private readonly HttpClient httpClient;
        private OAuthAccessToken currentToken;

        public GoogleOAuthClient(
            CloudSaveConfig configValue,
            OAuthTokenStore store
        )
        {
            config = configValue;
            tokenStore = store;
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        public bool HasStoredConnection()
        {
            return tokenStore.Exists();
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            string verifier = Base64Url(RandomBytes(64));
            byte[] verifierBytes = Encoding.ASCII.GetBytes(verifier);
            string challenge;
            using (SHA256 sha = SHA256.Create())
            {
                challenge = Base64Url(sha.ComputeHash(verifierBytes));
            }
            string state = Base64Url(RandomBytes(32));

            int port = ReserveLoopbackPort();
            string redirectUri = "http://127.0.0.1:" + port + "/";
            string authorizationUrl =
                AuthorizationEndpoint +
                "?client_id=" + Uri.EscapeDataString(config.google_client_id) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                "&response_type=code" +
                "&scope=" + Uri.EscapeDataString(config.google_scope) +
                "&code_challenge=" + Uri.EscapeDataString(challenge) +
                "&code_challenge_method=S256" +
                "&state=" + Uri.EscapeDataString(state) +
                "&access_type=offline" +
                "&prompt=consent";

            using (HttpListener listener = new HttpListener())
            {
                listener.Prefixes.Add(redirectUri);
                listener.Start();
                Process.Start(
                    new ProcessStartInfo(authorizationUrl)
                    {
                        UseShellExecute = true
                    }
                );

                Task<HttpListenerContext> callbackTask =
                    listener.GetContextAsync();
                Task timeoutTask = Task.Delay(
                    TimeSpan.FromMinutes(5),
                    cancellationToken
                );
                Task completed = await Task.WhenAny(
                    callbackTask,
                    timeoutTask
                );
                if (completed != callbackTask)
                {
                    listener.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        "A autorização do Google expirou após cinco minutos."
                    );
                }

                HttpListenerContext context = await callbackTask;
                string returnedState =
                    context.Request.QueryString["state"];
                string error = context.Request.QueryString["error"];
                string code = context.Request.QueryString["code"];

                if (
                    !String.Equals(
                        returnedState,
                        state,
                        StringComparison.Ordinal
                    )
                )
                {
                    WriteBrowserResponse(
                        context.Response,
                        false,
                        "O estado da autorização não confere."
                    );
                    throw new InvalidDataException(
                        "Resposta OAuth com estado inválido."
                    );
                }
                if (!String.IsNullOrWhiteSpace(error))
                {
                    WriteBrowserResponse(
                        context.Response,
                        false,
                        "A autorização foi cancelada."
                    );
                    throw new InvalidOperationException(
                        "O Google recusou a autorização: " + error
                    );
                }
                if (String.IsNullOrWhiteSpace(code))
                {
                    WriteBrowserResponse(
                        context.Response,
                        false,
                        "O código de autorização não foi recebido."
                    );
                    throw new InvalidDataException(
                        "Resposta OAuth sem código de autorização."
                    );
                }

                try
                {
                    currentToken = await ExchangeCodeAsync(
                        code,
                        verifier,
                        redirectUri,
                        cancellationToken
                    );
                    tokenStore.SaveRefreshToken(currentToken.RefreshToken);
                    WriteBrowserResponse(
                        context.Response,
                        true,
                        "Autorização concluída. Você pode fechar esta aba."
                    );
                }
                catch
                {
                    WriteBrowserResponse(
                        context.Response,
                        false,
                        "A autorização chegou ao aplicativo, mas a troca do " +
                        "token falhou. Volte ao MClonePC para ver o erro."
                    );
                    throw;
                }
            }
        }

        public async Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken
        )
        {
            if (currentToken != null && currentToken.IsUsable())
            {
                return currentToken.AccessToken;
            }

            string refreshToken = tokenStore.LoadRefreshToken();
            if (String.IsNullOrWhiteSpace(refreshToken))
            {
                throw new InvalidOperationException(
                    "Conecte uma conta Google antes de acessar a nuvem."
                );
            }

            Dictionary<string, string> values =
                new Dictionary<string, string>();
            values["client_id"] = config.google_client_id;
            values["refresh_token"] = refreshToken;
            values["grant_type"] = "refresh_token";

            currentToken = await RequestTokenAsync(
                values,
                refreshToken,
                cancellationToken
            );
            return currentToken.AccessToken;
        }

        public void Disconnect()
        {
            currentToken = null;
            tokenStore.Delete();
        }

        private async Task<OAuthAccessToken> ExchangeCodeAsync(
            string code,
            string verifier,
            string redirectUri,
            CancellationToken cancellationToken
        )
        {
            Dictionary<string, string> values =
                new Dictionary<string, string>();
            values["client_id"] = config.google_client_id;
            values["code"] = code;
            values["code_verifier"] = verifier;
            values["redirect_uri"] = redirectUri;
            values["grant_type"] = "authorization_code";
            return await RequestTokenAsync(
                values,
                null,
                cancellationToken
            );
        }

        private async Task<OAuthAccessToken> RequestTokenAsync(
            Dictionary<string, string> values,
            string existingRefreshToken,
            CancellationToken cancellationToken
        )
        {
            using (FormUrlEncodedContent content =
                new FormUrlEncodedContent(values))
            using (HttpResponseMessage response = await httpClient.PostAsync(
                TokenEndpoint,
                content,
                cancellationToken
            ))
            {
                string json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "Falha OAuth do Google (" +
                        (int)response.StatusCode + "): " +
                        SafeGoogleError(json)
                    );
                }

                Dictionary<string, object> result = DeserializeObject(json);
                string accessToken = GetString(result, "access_token");
                string refreshToken = GetString(result, "refresh_token");
                if (String.IsNullOrWhiteSpace(refreshToken))
                {
                    refreshToken = existingRefreshToken;
                }
                int expiresIn = GetInt(result, "expires_in", 3600);
                if (
                    String.IsNullOrWhiteSpace(accessToken) ||
                    String.IsNullOrWhiteSpace(refreshToken)
                )
                {
                    throw new InvalidDataException(
                        "Resposta OAuth incompleta."
                    );
                }
                return new OAuthAccessToken(
                    accessToken,
                    refreshToken,
                    DateTime.UtcNow.AddSeconds(expiresIn)
                );
            }
        }

        private static void WriteBrowserResponse(
            HttpListenerResponse response,
            bool success,
            string message
        )
        {
            string color = success ? "#287a3d" : "#a62d2d";
            string html =
                "<!doctype html><html><head><meta charset=\"utf-8\">" +
                "<title>MClonePC</title></head><body style=\"font-family:" +
                "Segoe UI,Arial;padding:40px;background:#f5f2e8\">" +
                "<h1 style=\"color:" + color + "\">MClonePC Save</h1>" +
                "<p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        private static int ReserveLoopbackPort()
        {
            TcpListener listener = new TcpListener(
                IPAddress.Loopback,
                0
            );
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            using (RandomNumberGenerator generator =
                RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }
            return bytes;
        }

        private static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string SafeGoogleError(string json)
        {
            try
            {
                Dictionary<string, object> root = DeserializeObject(json);
                object errorValue;
                if (!root.TryGetValue("error", out errorValue))
                {
                    return "resposta sem detalhes";
                }
                Dictionary<string, object> nested =
                    errorValue as Dictionary<string, object>;
                if (nested != null)
                {
                    string message = GetString(nested, "message");
                    if (!String.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
                return Convert.ToString(errorValue);
            }
            catch
            {
                return "resposta inválida";
            }
        }

        internal static Dictionary<string, object> DeserializeObject(
            string json
        )
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return serializer.Deserialize<Dictionary<string, object>>(json);
        }

        internal static string GetString(
            Dictionary<string, object> values,
            string key
        )
        {
            object value;
            return values.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value)
                : null;
        }

        internal static int GetInt(
            Dictionary<string, object> values,
            string key,
            int fallback
        )
        {
            object value;
            int parsed;
            return
                values.TryGetValue(key, out value) &&
                value != null &&
                Int32.TryParse(Convert.ToString(value), out parsed)
                    ? parsed
                    : fallback;
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }
    }

    internal sealed class RemoteSaveFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime? ModifiedTimeUtc { get; set; }
        public long Size { get; set; }
        public string Fingerprint { get; set; }
        public string BundleSha256 { get; set; }
        public int Revision { get; set; }
    }

    internal sealed class GoogleDriveAppDataClient : IDisposable
    {
        private const int MaximumDownloadBytes = 128 * 1024 * 1024;
        private const string DriveApi =
            "https://www.googleapis.com/drive/v3";
        private const string DriveUploadApi =
            "https://www.googleapis.com/upload/drive/v3";

        private readonly GoogleOAuthClient oauth;
        private readonly HttpClient httpClient;

        public GoogleDriveAppDataClient(GoogleOAuthClient oauthClient)
        {
            oauth = oauthClient;
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(2);
        }

        public async Task<RemoteSaveFile> FindAsync(
            string remoteFileName,
            CancellationToken cancellationToken
        )
        {
            string query = "name = '" +
                remoteFileName.Replace("'", "\\'") +
                "' and trashed = false";
            string url =
                DriveApi + "/files?spaces=appDataFolder" +
                "&q=" + Uri.EscapeDataString(query) +
                "&orderBy=modifiedTime%20desc" +
                "&pageSize=10" +
                "&fields=files(id,name,modifiedTime,size,appProperties)";
            using (HttpRequestMessage request = await AuthorizedRequestAsync(
                HttpMethod.Get,
                url,
                cancellationToken
            ))
            using (HttpResponseMessage response = await httpClient.SendAsync(
                request,
                cancellationToken
            ))
            {
                string json = await response.Content.ReadAsStringAsync();
                EnsureSuccess(response, json, "listar o save na nuvem");

                Dictionary<string, object> root =
                    GoogleOAuthClient.DeserializeObject(json);
                object filesValue;
                object[] files = root.TryGetValue("files", out filesValue)
                    ? filesValue as object[]
                    : null;
                if (files == null || files.Length == 0)
                {
                    return null;
                }
                Dictionary<string, object> file =
                    files[0] as Dictionary<string, object>;
                return file == null ? null : ParseRemoteFile(file);
            }
        }

        public async Task<RemoteSaveFile> UploadAsync(
            string remoteFileName,
            string existingFileId,
            byte[] bundle,
            string bundleSha256,
            string fingerprint,
            int revision,
            CancellationToken cancellationToken
        )
        {
            Dictionary<string, object> metadata =
                new Dictionary<string, object>();
            metadata["name"] = remoteFileName;
            if (String.IsNullOrWhiteSpace(existingFileId))
            {
                metadata["parents"] = new string[] { "appDataFolder" };
            }
            Dictionary<string, string> properties =
                new Dictionary<string, string>();
            properties["schemaVersion"] = "1";
            properties["bundleSha256"] = bundleSha256;
            properties["saveFingerprint"] = fingerprint;
            properties["revision"] = revision.ToString();
            properties["createdUtc"] = DateTime.UtcNow.ToString("o");
            metadata["appProperties"] = properties;

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string boundary = "mclonepc_" + Guid.NewGuid().ToString("N");
            using (MultipartContent multipart =
                new MultipartContent("related", boundary))
            {
                StringContent metadataContent = new StringContent(
                    serializer.Serialize(metadata),
                    Encoding.UTF8,
                    "application/json"
                );
                ByteArrayContent bundleContent = new ByteArrayContent(bundle);
                bundleContent.Headers.ContentType =
                    new MediaTypeHeaderValue("application/zip");
                multipart.Add(metadataContent);
                multipart.Add(bundleContent);

                bool updating = !String.IsNullOrWhiteSpace(existingFileId);
                string url = updating
                    ? DriveUploadApi + "/files/" +
                        Uri.EscapeDataString(existingFileId) +
                        "?uploadType=multipart" +
                        "&fields=id,name,modifiedTime,size,appProperties"
                    : DriveUploadApi + "/files?uploadType=multipart" +
                        "&fields=id,name,modifiedTime,size,appProperties";
                HttpMethod method = updating
                    ? new HttpMethod("PATCH")
                    : HttpMethod.Post;
                using (HttpRequestMessage request =
                    await AuthorizedRequestAsync(
                        method,
                        url,
                        cancellationToken
                    ))
                {
                    request.Content = multipart;
                    using (HttpResponseMessage response =
                        await httpClient.SendAsync(
                            request,
                            cancellationToken
                        ))
                    {
                        string json =
                            await response.Content.ReadAsStringAsync();
                        EnsureSuccess(
                            response,
                            json,
                            "enviar o save para a nuvem"
                        );
                        return ParseRemoteFile(
                            GoogleOAuthClient.DeserializeObject(json)
                        );
                    }
                }
            }
        }

        public async Task<byte[]> DownloadAsync(
            string fileId,
            CancellationToken cancellationToken
        )
        {
            string url = DriveApi + "/files/" +
                Uri.EscapeDataString(fileId) + "?alt=media";
            using (HttpRequestMessage request = await AuthorizedRequestAsync(
                HttpMethod.Get,
                url,
                cancellationToken
            ))
            using (HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            ))
            {
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    EnsureSuccess(
                        response,
                        error,
                        "baixar o save da nuvem"
                    );
                }
                if (
                    response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value >
                        MaximumDownloadBytes
                )
                {
                    throw new InvalidDataException(
                        "O pacote remoto excede o limite de 128 MB."
                    );
                }
                using (Stream input =
                    await response.Content.ReadAsStreamAsync())
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        int read = await input.ReadAsync(
                            buffer,
                            0,
                            buffer.Length,
                            cancellationToken
                        );
                        if (read == 0)
                        {
                            break;
                        }
                        if (output.Length + read > MaximumDownloadBytes)
                        {
                            throw new InvalidDataException(
                                "O pacote remoto excede o limite de 128 MB."
                            );
                        }
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }

        private async Task<HttpRequestMessage> AuthorizedRequestAsync(
            HttpMethod method,
            string url,
            CancellationToken cancellationToken
        )
        {
            string token = await oauth.GetAccessTokenAsync(
                cancellationToken
            );
            HttpRequestMessage request = new HttpRequestMessage(method, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        private static RemoteSaveFile ParseRemoteFile(
            Dictionary<string, object> file
        )
        {
            RemoteSaveFile result = new RemoteSaveFile();
            result.Id = GoogleOAuthClient.GetString(file, "id");
            result.Name = GoogleOAuthClient.GetString(file, "name");

            string modified = GoogleOAuthClient.GetString(
                file,
                "modifiedTime"
            );
            DateTime modifiedValue;
            if (
                DateTime.TryParse(
                    modified,
                    null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out modifiedValue
                )
            )
            {
                result.ModifiedTimeUtc = modifiedValue.ToUniversalTime();
            }

            long size;
            result.Size = Int64.TryParse(
                GoogleOAuthClient.GetString(file, "size"),
                out size
            ) ? size : 0;

            object propertiesValue;
            Dictionary<string, object> properties =
                file.TryGetValue("appProperties", out propertiesValue)
                    ? propertiesValue as Dictionary<string, object>
                    : null;
            if (properties != null)
            {
                result.Fingerprint = GoogleOAuthClient.GetString(
                    properties,
                    "saveFingerprint"
                );
                result.BundleSha256 = GoogleOAuthClient.GetString(
                    properties,
                    "bundleSha256"
                );
                result.Revision = GoogleOAuthClient.GetInt(
                    properties,
                    "revision",
                    0
                );
            }
            return result;
        }

        private static void EnsureSuccess(
            HttpResponseMessage response,
            string responseBody,
            string action
        )
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }
            throw new InvalidOperationException(
                "Não foi possível " + action + " (HTTP " +
                (int)response.StatusCode + "). " +
                ExtractDriveError(responseBody)
            );
        }

        private static string ExtractDriveError(string json)
        {
            try
            {
                Dictionary<string, object> root =
                    GoogleOAuthClient.DeserializeObject(json);
                object errorValue;
                Dictionary<string, object> error =
                    root.TryGetValue("error", out errorValue)
                        ? errorValue as Dictionary<string, object>
                        : null;
                string message = error == null
                    ? null
                    : GoogleOAuthClient.GetString(error, "message");
                return String.IsNullOrWhiteSpace(message)
                    ? "O Google Drive não enviou detalhes."
                    : message;
            }
            catch
            {
                return "Resposta inválida do Google Drive.";
            }
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }
    }

    internal sealed class SaveFileManifest
    {
        public string path { get; set; }
        public long size { get; set; }
        public string sha256 { get; set; }
        public string last_write_utc { get; set; }
    }

    internal sealed class SaveBundleManifest
    {
        public int schema_version { get; set; }
        public string product { get; set; }
        public string platform { get; set; }
        public string device_id { get; set; }
        public int revision { get; set; }
        public string created_utc { get; set; }
        public string save_fingerprint { get; set; }
        public List<SaveFileManifest> files { get; set; }
    }

    internal sealed class CreatedSaveBundle
    {
        public string FilePath { get; set; }
        public string BundleSha256 { get; set; }
        public string SaveFingerprint { get; set; }
        public int Revision { get; set; }
        public long Size { get; set; }
    }

    internal sealed class ValidatedSaveBundle
    {
        public string ExtractionRoot { get; set; }
        public string ExtractedSaveDirectory { get; set; }
        public SaveBundleManifest Manifest { get; set; }
        public string BundleSha256 { get; set; }
    }

    internal sealed class SaveBundleService
    {
        private const int MaximumFileCount = 10000;
        private const long MaximumExpandedBytes = 512L * 1024L * 1024L;
        private const string ManifestEntryName =
            "mclonepc-cloud-manifest.json";
        private readonly string deviceId;

        public SaveBundleService(string deviceIdentifier)
        {
            deviceId = deviceIdentifier;
        }

        public CreatedSaveBundle Create(
            string saveDirectory,
            string workingDirectory,
            int revision
        )
        {
            string source = Path.GetFullPath(saveDirectory).TrimEnd('\\');
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException(
                    "O diretório de save não existe: " + source
                );
            }

            string[] files = Directory.GetFiles(
                source,
                "*",
                SearchOption.AllDirectories
            );
            if (files.Length == 0)
            {
                throw new InvalidOperationException(
                    "O diretório de save está vazio."
                );
            }
            if (files.Length > MaximumFileCount)
            {
                throw new InvalidOperationException(
                    "O save excede o limite de 10.000 arquivos."
                );
            }
            long sourceBytes = 0;
            foreach (string file in files)
            {
                long length = new FileInfo(file).Length;
                if (length > MaximumExpandedBytes - sourceBytes)
                {
                    throw new InvalidOperationException(
                        "O save excede o limite total de 512 MB."
                    );
                }
                sourceBytes += length;
            }

            Directory.CreateDirectory(workingDirectory);
            string bundlePath = Path.Combine(
                workingDirectory,
                "mclonepc-save-" + Guid.NewGuid().ToString("N") + ".zip"
            );
            List<SaveFileManifest> manifestFiles =
                BuildFileManifest(source, files);
            string fingerprint = ComputeFingerprint(manifestFiles);

            SaveBundleManifest manifest = new SaveBundleManifest();
            manifest.schema_version = 1;
            manifest.product = "MClonePC";
            manifest.platform = "windows";
            manifest.device_id = deviceId;
            manifest.revision = revision;
            manifest.created_utc = DateTime.UtcNow.ToString("o");
            manifest.save_fingerprint = fingerprint;
            manifest.files = manifestFiles;

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            using (FileStream stream = new FileStream(
                bundlePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None
            ))
            using (ZipArchive archive = new ZipArchive(
                stream,
                ZipArchiveMode.Create,
                false,
                Encoding.UTF8
            ))
            {
                ZipArchiveEntry manifestEntry = archive.CreateEntry(
                    ManifestEntryName,
                    CompressionLevel.Optimal
                );
                using (StreamWriter writer = new StreamWriter(
                    manifestEntry.Open(),
                    new UTF8Encoding(false)
                ))
                {
                    writer.Write(serializer.Serialize(manifest));
                }

                foreach (SaveFileManifest item in manifestFiles)
                {
                    string absolute = Path.Combine(
                        source,
                        item.path.Replace('/', Path.DirectorySeparatorChar)
                    );
                    ZipArchiveEntry entry = archive.CreateEntry(
                        "save/" + item.path,
                        CompressionLevel.Optimal
                    );
                    entry.LastWriteTime = ParseManifestDate(
                        item.last_write_utc
                    );
                    using (Stream input = File.OpenRead(absolute))
                    using (Stream output = entry.Open())
                    {
                        input.CopyTo(output);
                    }
                }
            }

            CreatedSaveBundle result = new CreatedSaveBundle();
            result.FilePath = bundlePath;
            result.BundleSha256 = HashFile(bundlePath);
            result.SaveFingerprint = fingerprint;
            result.Revision = revision;
            result.Size = new FileInfo(bundlePath).Length;
            return result;
        }

        public ValidatedSaveBundle ValidateAndExtract(
            string bundlePath,
            string workingDirectory,
            string expectedBundleSha256
        )
        {
            string actualBundleHash = HashFile(bundlePath);
            if (
                !String.IsNullOrWhiteSpace(expectedBundleSha256) &&
                !FixedTimeEquals(
                    actualBundleHash,
                    expectedBundleSha256.ToLowerInvariant()
                )
            )
            {
                throw new InvalidDataException(
                    "O SHA-256 do pacote baixado não confere."
                );
            }

            string extractionRoot = Path.Combine(
                workingDirectory,
                "cloud-restore-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(extractionRoot);
            try
            {
                SaveBundleManifest manifest;
                using (ZipArchive archive = ZipFile.OpenRead(bundlePath))
                {
                    ZipArchiveEntry manifestEntry =
                        archive.GetEntry(ManifestEntryName);
                    if (manifestEntry == null)
                    {
                        throw new InvalidDataException(
                            "Manifesto do pacote de save ausente."
                        );
                    }
                    using (StreamReader reader = new StreamReader(
                        manifestEntry.Open(),
                        Encoding.UTF8,
                        true
                    ))
                    {
                        JavaScriptSerializer serializer =
                            new JavaScriptSerializer();
                        manifest = serializer.Deserialize<
                            SaveBundleManifest
                        >(reader.ReadToEnd());
                    }
                    ValidateManifest(manifest);

                    HashSet<string> expected =
                        new HashSet<string>(
                            manifest.files.Select(
                                item => "save/" + item.path
                            ),
                            StringComparer.Ordinal
                        );
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName == ManifestEntryName)
                        {
                            continue;
                        }
                        if (!expected.Contains(entry.FullName))
                        {
                            throw new InvalidDataException(
                                "Entrada inesperada no pacote: " +
                                entry.FullName
                            );
                        }
                        string destination = SafeDestinationPath(
                            extractionRoot,
                            entry.FullName
                        );
                        Directory.CreateDirectory(
                            Path.GetDirectoryName(destination)
                        );
                        using (Stream input = entry.Open())
                        using (FileStream output = new FileStream(
                            destination,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None
                        ))
                        {
                            input.CopyTo(output);
                        }
                    }
                }

                string extractedSave = Path.Combine(extractionRoot, "save");
                foreach (SaveFileManifest file in manifest.files)
                {
                    string absolute = SafeDestinationPath(
                        extractedSave,
                        file.path
                    );
                    if (!File.Exists(absolute))
                    {
                        throw new InvalidDataException(
                            "Arquivo ausente após extrair: " + file.path
                        );
                    }
                    FileInfo info = new FileInfo(absolute);
                    if (
                        info.Length != file.size ||
                        !FixedTimeEquals(HashFile(absolute), file.sha256)
                    )
                    {
                        throw new InvalidDataException(
                            "Arquivo divergente no pacote: " + file.path
                        );
                    }
                    File.SetLastWriteTimeUtc(
                        absolute,
                        DateTime.Parse(
                            file.last_write_utc,
                            null,
                            System.Globalization.DateTimeStyles
                                .RoundtripKind
                        ).ToUniversalTime()
                    );
                }
                string extractedFingerprint = ComputeFingerprint(
                    BuildFileManifest(
                        extractedSave,
                        Directory.GetFiles(
                            extractedSave,
                            "*",
                            SearchOption.AllDirectories
                        )
                    )
                );
                if (
                    !FixedTimeEquals(
                        extractedFingerprint,
                        manifest.save_fingerprint
                    )
                )
                {
                    throw new InvalidDataException(
                        "A impressão digital do save não confere."
                    );
                }

                ValidatedSaveBundle result = new ValidatedSaveBundle();
                result.ExtractionRoot = extractionRoot;
                result.ExtractedSaveDirectory = extractedSave;
                result.Manifest = manifest;
                result.BundleSha256 = actualBundleHash;
                return result;
            }
            catch
            {
                DeleteDirectoryIfExists(extractionRoot);
                throw;
            }
        }

        public string ComputeDirectoryFingerprint(string saveDirectory)
        {
            if (!Directory.Exists(saveDirectory))
            {
                return null;
            }
            string[] files = Directory.GetFiles(
                saveDirectory,
                "*",
                SearchOption.AllDirectories
            );
            if (files.Length == 0)
            {
                return null;
            }
            return ComputeFingerprint(
                BuildFileManifest(saveDirectory, files)
            );
        }

        private static List<SaveFileManifest> BuildFileManifest(
            string root,
            IEnumerable<string> files
        )
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd('\\');
            List<SaveFileManifest> result = new List<SaveFileManifest>();
            foreach (string file in files.OrderBy(
                value => value,
                StringComparer.OrdinalIgnoreCase
            ))
            {
                string absolute = Path.GetFullPath(file);
                if (
                    !absolute.StartsWith(
                        normalizedRoot + "\\",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    throw new InvalidDataException(
                        "Arquivo fora do diretório de save."
                    );
                }
                string relative = absolute.Substring(
                    normalizedRoot.Length + 1
                ).Replace('\\', '/');
                ValidateRelativePath(relative);

                FileInfo info = new FileInfo(absolute);
                SaveFileManifest item = new SaveFileManifest();
                item.path = relative;
                item.size = info.Length;
                item.sha256 = HashFile(absolute);
                item.last_write_utc =
                    info.LastWriteTimeUtc.ToString("o");
                result.Add(item);
            }
            return result;
        }

        private static string ComputeFingerprint(
            IEnumerable<SaveFileManifest> files
        )
        {
            StringBuilder canonical = new StringBuilder();
            foreach (SaveFileManifest file in files.OrderBy(
                item => item.path,
                StringComparer.Ordinal
            ))
            {
                canonical.Append(file.path);
                canonical.Append('\0');
                canonical.Append(file.size);
                canonical.Append('\0');
                canonical.Append(file.sha256);
                canonical.Append('\n');
            }
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(
                    sha.ComputeHash(
                        Encoding.UTF8.GetBytes(canonical.ToString())
                    )
                );
            }
        }

        private static void ValidateManifest(SaveBundleManifest manifest)
        {
            if (
                manifest == null ||
                manifest.schema_version != 1 ||
                manifest.product != "MClonePC" ||
                manifest.platform != "windows" ||
                manifest.revision < 1 ||
                String.IsNullOrWhiteSpace(manifest.device_id) ||
                String.IsNullOrWhiteSpace(manifest.save_fingerprint) ||
                manifest.files == null ||
                manifest.files.Count == 0 ||
                manifest.files.Count > MaximumFileCount
            )
            {
                throw new InvalidDataException(
                    "Manifesto do pacote de save inválido."
                );
            }
            if (
                manifest.save_fingerprint.Length != 64 ||
                manifest.files.Select(item => item.path)
                    .Distinct(StringComparer.Ordinal).Count() !=
                    manifest.files.Count
            )
            {
                throw new InvalidDataException(
                    "Manifesto contém hash ou caminhos inválidos."
                );
            }
            foreach (SaveFileManifest file in manifest.files)
            {
                ValidateRelativePath(file.path);
                if (
                    file.size < 0 ||
                    String.IsNullOrWhiteSpace(file.sha256) ||
                    file.sha256.Length != 64
                )
                {
                    throw new InvalidDataException(
                        "Metadados de arquivo inválidos: " + file.path
                    );
                }
                DateTime parsed;
                if (!DateTime.TryParse(
                    file.last_write_utc,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out parsed
                ))
                {
                    throw new InvalidDataException(
                        "Data inválida no manifesto: " + file.path
                    );
                }
            }
            long expandedBytes = 0;
            foreach (SaveFileManifest file in manifest.files)
            {
                if (
                    file.size > MaximumExpandedBytes - expandedBytes
                )
                {
                    throw new InvalidDataException(
                        "O pacote expandido excede o limite de 512 MB."
                    );
                }
                expandedBytes += file.size;
            }
        }

        private static void ValidateRelativePath(string relative)
        {
            if (
                String.IsNullOrWhiteSpace(relative) ||
                Path.IsPathRooted(relative) ||
                relative.Contains("\\") ||
                relative.Split('/').Any(
                    part =>
                        part.Length == 0 ||
                        part == "." ||
                        part == ".."
                )
            )
            {
                throw new InvalidDataException(
                    "Caminho relativo inválido: " + relative
                );
            }
        }

        private static string SafeDestinationPath(
            string root,
            string relative
        )
        {
            ValidateRelativePath(relative);
            string normalizedRoot = Path.GetFullPath(root).TrimEnd('\\');
            string destination = Path.GetFullPath(
                Path.Combine(
                    normalizedRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar)
                )
            );
            if (
                !destination.StartsWith(
                    normalizedRoot + "\\",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidDataException(
                    "Entrada do pacote escaparia do diretório de destino."
                );
            }
            return destination;
        }

        private static DateTimeOffset ParseManifestDate(string value)
        {
            DateTime parsed = DateTime.Parse(
                value,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind
            );
            return new DateTimeOffset(parsed.ToUniversalTime());
        }

        internal static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                result.Append(value.ToString("x2"));
            }
            return result.ToString();
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (
                left == null ||
                right == null ||
                left.Length != right.Length
            )
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        internal static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    internal sealed class RestoreService
    {
        public string Restore(
            ValidatedSaveBundle bundle,
            string targetSaveDirectory,
            string backupRoot
        )
        {
            string target = Path.GetFullPath(
                targetSaveDirectory
            ).TrimEnd('\\');
            string parent = Path.GetDirectoryName(target);
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(backupRoot);

            string rollback = Path.Combine(
                parent,
                "." + Path.GetFileName(target) + ".cloud-rollback-" +
                Guid.NewGuid().ToString("N")
            );
            string backup = null;
            bool movedExisting = false;
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Move(target, rollback);
                    movedExisting = true;
                }
                Directory.Move(bundle.ExtractedSaveDirectory, target);

                if (movedExisting)
                {
                    backup = Path.Combine(
                        backupRoot,
                        DateTime.UtcNow.ToString(
                            "yyyyMMdd-HHmmss-fff"
                        ) + "-" +
                        bundle.Manifest.revision.ToString()
                    );
                    CopyDirectory(rollback, backup);
                    Directory.Delete(rollback, true);
                }
                RetainNewestBackups(backupRoot, 3);
                return backup;
            }
            catch
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }
                if (movedExisting && Directory.Exists(rollback))
                {
                    Directory.Move(rollback, target);
                }
                throw;
            }
            finally
            {
                SaveBundleService.DeleteDirectoryIfExists(
                    bundle.ExtractionRoot
                );
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(
                source,
                "*",
                SearchOption.AllDirectories
            ))
            {
                string relative = directory.Substring(source.Length + 1);
                Directory.CreateDirectory(
                    Path.Combine(destination, relative)
                );
            }
            foreach (string file in Directory.GetFiles(
                source,
                "*",
                SearchOption.AllDirectories
            ))
            {
                string relative = file.Substring(source.Length + 1);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
            }
        }

        private static void RetainNewestBackups(
            string backupRoot,
            int maximum
        )
        {
            DirectoryInfo[] backups = new DirectoryInfo(backupRoot)
                .GetDirectories()
                .OrderByDescending(item => item.Name)
                .ToArray();
            for (int index = maximum; index < backups.Length; index++)
            {
                backups[index].Delete(true);
            }
        }
    }

    internal static class CloudSaveRuntime
    {
        public static bool IsGameRunning()
        {
            Process[] processes = Process.GetProcessesByName("mclonepc");
            try
            {
                return processes.Length != 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        public static string GetStateDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "Robson",
                "MClonePC",
                "CloudSync"
            );
        }

        public static string GetOrCreateDeviceId(string stateDirectory)
        {
            Directory.CreateDirectory(stateDirectory);
            string path = Path.Combine(stateDirectory, "device-id.txt");
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path, Encoding.ASCII).Trim();
                Guid parsed;
                if (Guid.TryParse(existing, out parsed))
                {
                    return parsed.ToString("N");
                }
            }
            string created = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, created, Encoding.ASCII);
            return created;
        }
    }
}
