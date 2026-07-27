package plugin.mclonecloud;

import android.accounts.Account;
import android.app.Activity;
import android.app.PendingIntent;
import android.content.Intent;

import com.ansca.corona.CoronaActivity;
import com.ansca.corona.CoronaEnvironment;
import com.google.android.gms.auth.api.identity.AuthorizationClient;
import com.google.android.gms.auth.api.identity.AuthorizationRequest;
import com.google.android.gms.auth.api.identity.AuthorizationResult;
import com.google.android.gms.auth.api.identity.Identity;
import com.google.android.gms.auth.api.identity.RevokeAccessRequest;
import com.google.android.gms.auth.api.signin.GoogleSignInAccount;
import com.google.android.gms.common.api.Scope;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.text.ParsePosition;
import java.text.SimpleDateFormat;
import java.util.Collections;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import javax.net.ssl.HttpsURLConnection;

/**
 * Android Google Drive transport for the Lua cloud-save adapter.
 *
 * Authorization is delegated to Google Identity Services on the UI thread.
 * Drive requests run serially off the UI thread and use the same JSON contract
 * and appDataFolder file as the Windows implementation.
 */
final class AndroidCloudClient {
    interface Callback {
        void complete(String responseJson);
    }

    private interface AuthorizationCallback {
        void authorized(String accessToken);
        void failed(Exception exception);
    }

    private static final String DRIVE_API =
        "https://www.googleapis.com/drive/v3";
    private static final String DRIVE_UPLOAD_API =
        "https://www.googleapis.com/upload/drive/v3";
    private static final String DRIVE_SCOPE =
        "https://www.googleapis.com/auth/drive.appdata";
    private static final String REMOTE_FILE_NAME =
        "mclonepc-game-cloud-v1.json";
    private static final int MAXIMUM_PAYLOAD_BYTES = 64 * 1024 * 1024;
    private static final int AUTHENTICATION_ERROR_STATUS = 2001;
    private static final long ACCESS_TOKEN_CACHE_MS = 50L * 60L * 1000L;
    private static final long REMOTE_FILE_CACHE_MS = 15L * 1000L;
    private static final AndroidCloudClient INSTANCE =
        new AndroidCloudClient();

    private final ExecutorService executor =
        Executors.newSingleThreadExecutor();
    private final List<Scope> requestedScopes =
        Collections.singletonList(new Scope(DRIVE_SCOPE));
    private final AccessTokenCache accessTokenCache =
        new AccessTokenCache(ACCESS_TOKEN_CACHE_MS);
    private RemoteFile cachedRemoteFile;
    private boolean cachedRemoteFileKnown;
    private long cachedRemoteFileAtMs;

    static AndroidCloudClient getInstance() {
        return INSTANCE;
    }

    void execute(
        final String action,
        final String payload,
        final Callback callback
    ) {
        if ("logout".equals(action)) {
            disconnect(callback);
            return;
        }
        executeAuthorizedAction(action, payload, callback, false);
    }

    private void executeAuthorizedAction(
        final String action,
        final String payload,
        final Callback callback,
        final boolean retryingAfterRejectedToken
    ) {
        authorize(
            "connect".equals(action),
            new AuthorizationCallback() {
                @Override
                public void authorized(final String accessToken) {
                    executor.execute(
                        () -> {
                            try {
                                callback.complete(
                                    success(
                                        perform(
                                            action,
                                            payload,
                                            accessToken
                                        )
                                    ).toString()
                                );
                            } catch (Exception exception) {
                                if (
                                    !retryingAfterRejectedToken &&
                                    isRejectedAccessToken(exception)
                                ) {
                                    clearSessionCache();
                                    executeAuthorizedAction(
                                        action,
                                        payload,
                                        callback,
                                        true
                                    );
                                    return;
                                }
                                callback.complete(
                                    failure(
                                        safeMessage(exception),
                                        isAuthenticationError(exception)
                                            ? AUTHENTICATION_ERROR_STATUS
                                            : 0
                                    ).toString()
                                );
                            }
                        }
                    );
                }

                @Override
                public void failed(Exception exception) {
                    callback.complete(
                        failure(
                            safeMessage(exception),
                            AUTHENTICATION_ERROR_STATUS
                        ).toString()
                    );
                }
            }
        );
    }

    private JSONObject perform(
        String action,
        String payload,
        String accessToken
    ) throws Exception {
        if (
            "connect".equals(action) ||
            "verify".equals(action) ||
            "status".equals(action)
        ) {
            return getStatus(accessToken);
        }
        if ("upload".equals(action)) {
            validatePayload(payload);
            RemoteFile remote = findRemoteFile(accessToken);
            int revision = remote == null
                ? 1
                : Math.max(1, remote.revision + 1);
            byte[] payloadBytes = payload.getBytes(StandardCharsets.UTF_8);
            RemoteFile uploaded = upload(
                remote == null ? null : remote.id,
                payloadBytes,
                sha256(payloadBytes),
                revision,
                accessToken
            );
            return remoteResult(uploaded);
        }
        if ("download".equals(action)) {
            RemoteFile remote = getCachedRemoteFile();
            if (!isRemoteFileCacheFresh()) {
                remote = findRemoteFile(accessToken);
            }
            if (remote == null) {
                return new JSONObject()
                    .put("connected", true)
                    .put("noBackup", true);
            }
            String downloaded = new String(
                download(remote.id, accessToken),
                StandardCharsets.UTF_8
            );
            validatePayload(downloaded);
            return remoteResult(remote).put("payload", downloaded);
        }
        throw new IllegalArgumentException(
            "Ação de nuvem Android desconhecida: " + action
        );
    }

    private void authorize(
        final boolean allowResolution,
        final AuthorizationCallback callback
    ) {
        String accessToken = getCachedAccessToken();
        if (accessToken != null) {
            callback.authorized(accessToken);
            return;
        }
        final CoronaActivity activity = requireActivity(callback);
        if (activity == null) {
            return;
        }
        activity.runOnUiThread(
            () -> {
                final AuthorizationClient client =
                    Identity.getAuthorizationClient(activity);
                AuthorizationRequest request =
                    AuthorizationRequest.builder()
                        .setRequestedScopes(requestedScopes)
                        .build();
                client.authorize(request)
                    .addOnSuccessListener(
                        result -> handleAuthorizationResult(
                            activity,
                            client,
                            result,
                            allowResolution,
                            callback
                        )
                    )
                    .addOnFailureListener(
                        exception -> callback.failed(
                            authenticationFailure(exception)
                        )
                    );
            }
        );
    }

    private void handleAuthorizationResult(
        final CoronaActivity activity,
        final AuthorizationClient client,
        final AuthorizationResult result,
        final boolean allowResolution,
        final AuthorizationCallback callback
    ) {
        if (!result.hasResolution()) {
            completeAuthorization(result, callback);
            return;
        }
        if (!allowResolution) {
            callback.failed(
                new AuthenticationException(
                    "Conecte uma conta Google antes de acessar a nuvem."
                )
            );
            return;
        }
        final PendingIntent pendingIntent = result.getPendingIntent();
        if (pendingIntent == null) {
            callback.failed(
                new AuthenticationException(
                    "O Google não forneceu a tela de autorização."
                )
            );
            return;
        }
        final CoronaActivity.OnActivityResultHandler handler =
            new CoronaActivity.OnActivityResultHandler() {
                @Override
                public void onHandleActivityResult(
                    CoronaActivity owner,
                    int requestCode,
                    int resultCode,
                    Intent data
                ) {
                    owner.unregisterActivityResultHandler(this);
                    if (resultCode != Activity.RESULT_OK || data == null) {
                        callback.failed(
                            new AuthenticationException(
                                "A conexão com o Google foi cancelada."
                            )
                        );
                        return;
                    }
                    try {
                        completeAuthorization(
                            client.getAuthorizationResultFromIntent(data),
                            callback
                        );
                    } catch (Exception exception) {
                        callback.failed(
                            authenticationFailure(exception)
                        );
                    }
                }
            };
        int requestCode = activity.registerActivityResultHandler(handler);
        try {
            activity.startIntentSenderForResult(
                pendingIntent.getIntentSender(),
                requestCode,
                null,
                0,
                0,
                0
            );
        } catch (Exception exception) {
            activity.unregisterActivityResultHandler(handler);
            callback.failed(authenticationFailure(exception));
        }
    }

    private void completeAuthorization(
        AuthorizationResult result,
        AuthorizationCallback callback
    ) {
        String accessToken = result.getAccessToken();
        if (accessToken == null || accessToken.length() == 0) {
            callback.failed(
                new AuthenticationException(
                    "O Google não devolveu um token de acesso."
                )
            );
            return;
        }
        cacheAccessToken(accessToken);
        callback.authorized(accessToken);
    }

    private void disconnect(final Callback callback) {
        clearSessionCache();
        final CoronaActivity activity = requireActivity(
            new AuthorizationCallback() {
                @Override
                public void authorized(String accessToken) {
                }

                @Override
                public void failed(Exception exception) {
                    callback.complete(
                        failure(
                            safeMessage(exception),
                            AUTHENTICATION_ERROR_STATUS
                        ).toString()
                    );
                }
            }
        );
        if (activity == null) {
            return;
        }
        activity.runOnUiThread(
            () -> {
                final AuthorizationClient client =
                    Identity.getAuthorizationClient(activity);
                AuthorizationRequest request =
                    AuthorizationRequest.builder()
                        .setRequestedScopes(requestedScopes)
                        .build();
                client.authorize(request)
                    .addOnSuccessListener(
                        result -> revokeAuthorizedAccount(
                            client,
                            result,
                            callback
                        )
                    )
                    .addOnFailureListener(
                        exception -> callback.complete(
                            failure(
                                safeMessage(
                                    authenticationFailure(exception)
                                ),
                                AUTHENTICATION_ERROR_STATUS
                            ).toString()
                        )
                    );
            }
        );
    }

    private void revokeAuthorizedAccount(
        AuthorizationClient client,
        AuthorizationResult result,
        Callback callback
    ) {
        if (result.hasResolution()) {
            callback.complete(disconnectedResult().toString());
            return;
        }
        GoogleSignInAccount signInAccount =
            result.toGoogleSignInAccount();
        Account account = signInAccount == null
            ? null
            : signInAccount.getAccount();
        if (account == null) {
            callback.complete(disconnectedResult().toString());
            return;
        }
        RevokeAccessRequest request = RevokeAccessRequest.builder()
            .setAccount(account)
            .setScopes(requestedScopes)
            .build();
        client.revokeAccess(request)
            .addOnSuccessListener(
                ignored -> callback.complete(
                    disconnectedResult().toString()
                )
            )
            .addOnFailureListener(
                exception -> callback.complete(
                    failure(
                        safeMessage(authenticationFailure(exception)),
                        AUTHENTICATION_ERROR_STATUS
                    ).toString()
                )
            );
    }

    private JSONObject getStatus(String accessToken) throws Exception {
        RemoteFile remote = findRemoteFile(accessToken);
        if (remote == null) {
            return new JSONObject()
                .put("connected", true)
                .put("noBackup", true);
        }
        return remoteResult(remote);
    }

    private RemoteFile findRemoteFile(String accessToken) throws Exception {
        String query =
            "name = '" + REMOTE_FILE_NAME.replace("'", "\\'") +
            "' and trashed = false";
        String url =
            DRIVE_API + "/files?spaces=appDataFolder" +
            "&q=" + encode(query) +
            "&orderBy=modifiedTime%20desc" +
            "&pageSize=10" +
            "&fields=files(id,name,modifiedTime,size,appProperties)";
        JSONObject response = authorizedJson(
            url,
            "GET",
            null,
            null,
            accessToken
        );
        JSONArray files = response.optJSONArray("files");
        if (files == null || files.length() == 0) {
            cacheRemoteFile(null);
            return null;
        }
        RemoteFile remote = parseRemote(files.getJSONObject(0));
        cacheRemoteFile(remote);
        return remote;
    }

    private RemoteFile upload(
        String existingId,
        byte[] payload,
        String hash,
        int revision,
        String accessToken
    ) throws Exception {
        JSONObject properties = new JSONObject()
            .put("schemaVersion", "1")
            .put("bundleSha256", hash)
            .put("saveFingerprint", hash)
            .put("revision", Integer.toString(revision))
            .put("createdUtc", isoUtcNow());
        JSONObject metadata = new JSONObject()
            .put("name", REMOTE_FILE_NAME)
            .put("appProperties", properties);
        if (existingId == null) {
            metadata.put(
                "parents",
                new JSONArray().put("appDataFolder")
            );
        }

        String boundary = "mclonepc_" + Long.toHexString(
            System.nanoTime()
        );
        ByteArrayOutputStream body = new ByteArrayOutputStream(
            metadata.toString().length() + payload.length + 512
        );
        body.write(("--" + boundary + "\r\n")
            .getBytes(StandardCharsets.UTF_8));
        body.write(
            "Content-Type: application/json; charset=UTF-8\r\n\r\n"
                .getBytes(StandardCharsets.UTF_8)
        );
        body.write(metadata.toString().getBytes(StandardCharsets.UTF_8));
        body.write(("\r\n--" + boundary + "\r\n")
            .getBytes(StandardCharsets.UTF_8));
        body.write(
            "Content-Type: application/json\r\n\r\n"
                .getBytes(StandardCharsets.UTF_8)
        );
        body.write(payload);
        body.write(("\r\n--" + boundary + "--\r\n")
            .getBytes(StandardCharsets.UTF_8));

        String url;
        String method;
        if (existingId == null) {
            url = DRIVE_UPLOAD_API +
                "/files?uploadType=multipart" +
                "&fields=id,name,modifiedTime,size,appProperties";
            method = "POST";
        } else {
            url = DRIVE_UPLOAD_API + "/files/" + encode(existingId) +
                "?uploadType=multipart" +
                "&fields=id,name,modifiedTime,size,appProperties";
            method = "PATCH";
        }
        RemoteFile uploaded = parseRemote(
            authorizedJson(
                url,
                method,
                "multipart/related; boundary=" + boundary,
                body.toByteArray(),
                accessToken
            )
        );
        cacheRemoteFile(uploaded);
        return uploaded;
    }

    private byte[] download(
        String fileId,
        String accessToken
    ) throws Exception {
        HttpsURLConnection connection = openHttps(
            DRIVE_API + "/files/" + encode(fileId) + "?alt=media",
            "GET",
            accessToken
        );
        int status = connection.getResponseCode();
        if (status < 200 || status >= 300) {
            throw driveError(connection, status);
        }
        return readLimited(
            new BufferedInputStream(connection.getInputStream()),
            MAXIMUM_PAYLOAD_BYTES
        );
    }

    private JSONObject authorizedJson(
        String url,
        String method,
        String contentType,
        byte[] body,
        String accessToken
    ) throws Exception {
        HttpsURLConnection connection = openHttps(
            url,
            method,
            accessToken
        );
        if (contentType != null) {
            connection.setRequestProperty("Content-Type", contentType);
        }
        if (body != null) {
            connection.setFixedLengthStreamingMode(body.length);
            writeBody(connection, body);
        }
        return readJsonResponse(connection);
    }

    private HttpsURLConnection openHttps(
        String rawUrl,
        String method,
        String bearerToken
    ) throws Exception {
        URL url = new URL(rawUrl);
        if (!"https".equalsIgnoreCase(url.getProtocol())) {
            throw new SecurityException("A nuvem exige HTTPS.");
        }
        HttpsURLConnection connection =
            (HttpsURLConnection) url.openConnection();
        connection.setConnectTimeout(15_000);
        connection.setReadTimeout(120_000);
        connection.setUseCaches(false);
        connection.setInstanceFollowRedirects(false);
        if ("PATCH".equals(method)) {
            connection.setRequestMethod("POST");
            connection.setRequestProperty(
                "X-HTTP-Method-Override",
                "PATCH"
            );
        } else {
            connection.setRequestMethod(method);
        }
        connection.setRequestProperty(
            "Authorization",
            "Bearer " + bearerToken
        );
        return connection;
    }

    private static void writeBody(
        HttpURLConnection connection,
        byte[] body
    ) throws Exception {
        connection.setDoOutput(true);
        try (
            OutputStream output = new BufferedOutputStream(
                connection.getOutputStream()
            )
        ) {
            output.write(body);
        }
    }

    private JSONObject readJsonResponse(HttpURLConnection connection)
        throws Exception {
        int status = connection.getResponseCode();
        InputStream stream =
            status >= 200 && status < 300
                ? connection.getInputStream()
                : connection.getErrorStream();
        byte[] bytes = readLimited(
            stream == null
                ? null
                : new BufferedInputStream(stream),
            2 * 1024 * 1024
        );
        String text = new String(bytes, StandardCharsets.UTF_8);
        if (status < 200 || status >= 300) {
            throw new DriveHttpException(
                status,
                "Falha Google Drive (" + status + "): " +
                safeGoogleError(text)
            );
        }
        return new JSONObject(text);
    }

    private Exception driveError(
        HttpURLConnection connection,
        int status
    ) throws Exception {
        byte[] bytes = readLimited(
            connection.getErrorStream() == null
                ? null
                : new BufferedInputStream(connection.getErrorStream()),
            2 * 1024 * 1024
        );
        return new DriveHttpException(
            status,
            "Falha Google Drive (" + status + "): " +
            safeGoogleError(new String(bytes, StandardCharsets.UTF_8))
        );
    }

    private static byte[] readLimited(InputStream input, int maximum)
        throws Exception {
        if (input == null) {
            return new byte[0];
        }
        try (
            InputStream source = input;
            ByteArrayOutputStream output = new ByteArrayOutputStream()
        ) {
            byte[] buffer = new byte[32 * 1024];
            int count;
            while ((count = source.read(buffer)) >= 0) {
                if (output.size() + count > maximum) {
                    throw new IllegalStateException(
                        "A resposta da nuvem excedeu o limite."
                    );
                }
                output.write(buffer, 0, count);
            }
            return output.toByteArray();
        }
    }

    private RemoteFile parseRemote(JSONObject value) {
        RemoteFile remote = new RemoteFile();
        remote.id = value.optString("id", null);
        remote.modifiedTime = value.optString("modifiedTime", null);
        JSONObject properties = value.optJSONObject("appProperties");
        remote.revision = properties == null
            ? 0
            : parseInteger(properties.optString("revision", "0"));
        return remote;
    }

    private JSONObject remoteResult(RemoteFile remote) throws Exception {
        return new JSONObject()
            .put("connected", true)
            .put("noBackup", false)
            .put("lastSaveTime", parseUnixSeconds(remote.modifiedTime))
            .put("revision", Math.max(1, remote.revision));
    }

    private void validatePayload(String payload) throws Exception {
        if (payload == null) {
            throw new IllegalArgumentException(
                "O payload do save está ausente."
            );
        }
        byte[] bytes = payload.getBytes(StandardCharsets.UTF_8);
        if (bytes.length == 0 || bytes.length > MAXIMUM_PAYLOAD_BYTES) {
            throw new IllegalArgumentException(
                "Tamanho inválido do payload do save."
            );
        }
        JSONObject value = new JSONObject(payload);
        if (
            value.optInt("schema_version", 0) != 1 ||
            !value.has("gameData") ||
            !value.has("info")
        ) {
            throw new IllegalArgumentException(
                "Payload do save incompatível."
            );
        }
    }

    private CoronaActivity requireActivity(
        AuthorizationCallback callback
    ) {
        CoronaActivity activity = CoronaEnvironment.getCoronaActivity();
        if (activity == null) {
            callback.failed(
                new AuthenticationException(
                    "A tela do jogo não está disponível para abrir o Google."
                )
            );
        }
        return activity;
    }

    private JSONObject disconnectedResult() {
        try {
            return success(
                new JSONObject()
                    .put("connected", false)
                    .put("noBackup", true)
            );
        } catch (Exception impossible) {
            return new JSONObject();
        }
    }

    private static AuthenticationException authenticationFailure(
        Exception exception
    ) {
        return new AuthenticationException(
            "Falha ao autorizar o Google: " + safeMessage(exception)
        );
    }

    private static JSONObject success(JSONObject result) throws Exception {
        return new JSONObject()
            .put("ok", true)
            .put("result", result)
            .put("status", 0);
    }

    private static JSONObject failure(String error, int status) {
        try {
            return new JSONObject()
                .put("ok", false)
                .put("error", error)
                .put("status", status);
        } catch (Exception impossible) {
            return new JSONObject();
        }
    }

    private static boolean isAuthenticationError(Exception exception) {
        return
            exception instanceof AuthenticationException ||
            isRejectedAccessToken(exception);
    }

    private static boolean isRejectedAccessToken(Exception exception) {
        return
            exception instanceof DriveHttpException &&
            ((DriveHttpException) exception).status == 401;
    }

    private synchronized String getCachedAccessToken() {
        return accessTokenCache.get();
    }

    private synchronized void cacheAccessToken(String accessToken) {
        accessTokenCache.put(accessToken);
    }

    private synchronized void cacheRemoteFile(RemoteFile remote) {
        cachedRemoteFile = remote;
        cachedRemoteFileKnown = true;
        cachedRemoteFileAtMs = System.currentTimeMillis();
    }

    private synchronized boolean isRemoteFileCacheFresh() {
        return
            cachedRemoteFileKnown &&
            System.currentTimeMillis() - cachedRemoteFileAtMs <
                REMOTE_FILE_CACHE_MS;
    }

    private synchronized RemoteFile getCachedRemoteFile() {
        return cachedRemoteFile;
    }

    private synchronized void clearSessionCache() {
        accessTokenCache.clear();
        cachedRemoteFile = null;
        cachedRemoteFileKnown = false;
        cachedRemoteFileAtMs = 0L;
    }

    private static String safeMessage(Exception exception) {
        String message = exception.getMessage();
        return message == null || message.length() == 0
            ? exception.getClass().getSimpleName()
            : message;
    }

    private static String safeGoogleError(String text) {
        try {
            JSONObject root = new JSONObject(text);
            Object value = root.opt("error");
            if (value instanceof JSONObject) {
                String message = ((JSONObject) value).optString(
                    "message",
                    null
                );
                if (message != null && message.length() > 0) {
                    return message;
                }
            }
            return value == null
                ? "resposta sem detalhes"
                : String.valueOf(value);
        } catch (Exception ignored) {
            return "resposta inválida";
        }
    }

    private static String encode(String value) throws Exception {
        return URLEncoder.encode(value, "UTF-8").replace("+", "%20");
    }

    private static String sha256(byte[] bytes) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(bytes);
        StringBuilder output = new StringBuilder(digest.length * 2);
        for (byte value : digest) {
            output.append(String.format(Locale.US, "%02x", value & 0xff));
        }
        return output.toString();
    }

    private static int parseInteger(String value) {
        try {
            return Integer.parseInt(value);
        } catch (Exception ignored) {
            return 0;
        }
    }

    private static long parseUnixSeconds(String value) {
        if (value == null || value.length() == 0) {
            return System.currentTimeMillis() / 1000L;
        }
        String[] patterns = {
            "yyyy-MM-dd'T'HH:mm:ss.SSSX",
            "yyyy-MM-dd'T'HH:mm:ssX"
        };
        for (String pattern : patterns) {
            SimpleDateFormat format = new SimpleDateFormat(
                pattern,
                Locale.US
            );
            format.setTimeZone(TimeZone.getTimeZone("UTC"));
            ParsePosition position = new ParsePosition(0);
            Date parsed = format.parse(value, position);
            if (parsed != null && position.getIndex() == value.length()) {
                return parsed.getTime() / 1000L;
            }
        }
        return System.currentTimeMillis() / 1000L;
    }

    private static String isoUtcNow() {
        SimpleDateFormat format = new SimpleDateFormat(
            "yyyy-MM-dd'T'HH:mm:ss.SSS'Z'",
            Locale.US
        );
        format.setTimeZone(TimeZone.getTimeZone("UTC"));
        return format.format(new Date());
    }

    private static final class RemoteFile {
        String id;
        String modifiedTime;
        int revision;
    }

    private static final class AuthenticationException extends Exception {
        AuthenticationException(String message) {
            super(message);
        }
    }

    private static final class DriveHttpException extends Exception {
        final int status;

        DriveHttpException(int statusCode, String message) {
            super(message);
            status = statusCode;
        }
    }
}
