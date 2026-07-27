package io.github.robsonc.mclonepc;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.IntentSender;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicBoolean;

public final class UpdateGateActivity extends Activity {
    static final String EXTRA_SKIP_CHECK_ONCE =
        "io.github.robsonc.mclonepc.SKIP_UPDATE_CHECK_ONCE";
    static final String INSTALL_ACTION =
        "io.github.robsonc.mclonepc.UPDATE_INSTALL_STATUS";

    private static final String TAG = "MClonePC.Update";
    private static final String MANIFEST_URL =
        "https://github.com/Robson-C/mclonepc-learning/releases/latest/" +
        "download/update.json";
    private static final int CHECK_CONNECT_TIMEOUT_MS = 1200;
    private static final int CHECK_READ_TIMEOUT_MS = 1200;
    private static final int CHECK_HARD_LIMIT_MS = 2500;
    private static final int DOWNLOAD_CONNECT_TIMEOUT_MS = 10000;
    private static final int DOWNLOAD_READ_TIMEOUT_MS = 30000;
    private static final int MAX_MANIFEST_BYTES = 256 * 1024;
    private static final long MAX_APK_BYTES = 500L * 1024L * 1024L;
    private static final int REQUEST_UNKNOWN_SOURCES = 4817;

    private final AtomicBoolean startupDecisionMade = new AtomicBoolean(false);
    private TextView statusView;
    private File pendingApk;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        createBlackScreen();
        boolean firstGateInProcess = ProcessUpdateCheckGuard.claim();
        if (getIntent().getBooleanExtra(EXTRA_SKIP_CHECK_ONCE, false)) {
            launchGame();
            return;
        }
        if (!firstGateInProcess) {
            Log.i(TAG, "Update check already handled by this process.");
            launchGame();
            return;
        }

        statusView.setText("");
        statusView.postDelayed(
            new Runnable() {
                @Override
                public void run() {
                    continueWithoutUpdate("check hard timeout");
                }
            },
            CHECK_HARD_LIMIT_MS
        );

        Thread checkThread = new Thread(
            new Runnable() {
                @Override
                public void run() {
                    checkForUpdate();
                }
            },
            "mclone-update-check"
        );
        checkThread.setDaemon(true);
        checkThread.start();
    }

    private void createBlackScreen() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        statusView = new TextView(this);
        statusView.setTextColor(Color.WHITE);
        statusView.setTextSize(22.0f);
        statusView.setGravity(Gravity.CENTER);
        statusView.setBackgroundColor(Color.BLACK);
        FrameLayout.LayoutParams parameters = new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        );
        root.addView(statusView, parameters);
        setContentView(root);
    }

    private void checkForUpdate() {
        try {
            byte[] manifestBytes = downloadBytes(
                MANIFEST_URL,
                CHECK_CONNECT_TIMEOUT_MS,
                CHECK_READ_TIMEOUT_MS,
                MAX_MANIFEST_BYTES
            );
            JSONObject manifest = new JSONObject(
                new String(manifestBytes, StandardCharsets.UTF_8)
            );
            UpdateArtifact artifact = parseAndroidArtifact(manifest);
            if (artifact.versionCode <= getInstalledVersionCode()) {
                continueWithoutUpdate("already current");
                return;
            }
            if (!startupDecisionMade.compareAndSet(false, true)) {
                return;
            }
            runOnUiThread(
                new Runnable() {
                    @Override
                    public void run() {
                        statusView.setText("Atualizando...");
                    }
                }
            );
            downloadAndInstall(artifact);
        } catch (Exception exception) {
            Log.w(TAG, "Update check failed; starting installed game.", exception);
            continueWithoutUpdate("check failed");
        }
    }

    private void downloadAndInstall(UpdateArtifact artifact) {
        File updateDirectory = new File(getCacheDir(), "updates");
        File temporary = new File(updateDirectory, "MClonePC.apk.download");
        File completed = new File(updateDirectory, "MClonePC.apk");
        try {
            if (!updateDirectory.isDirectory() && !updateDirectory.mkdirs()) {
                throw new IllegalStateException(
                    "Unable to create update cache directory."
                );
            }
            deleteIfPresent(temporary);
            deleteIfPresent(completed);

            downloadApk(artifact, temporary);
            validateDownloadedApk(artifact, temporary);
            if (!temporary.renameTo(completed)) {
                copyFile(temporary, completed);
                deleteIfPresent(temporary);
            }
            pendingApk = completed;
            runOnUiThread(
                new Runnable() {
                    @Override
                    public void run() {
                        requestInstallPermissionOrInstall();
                    }
                }
            );
        } catch (Exception exception) {
            Log.e(TAG, "Update download/install preparation failed.", exception);
            deleteIfPresent(temporary);
            deleteIfPresent(completed);
            runOnUiThread(
                new Runnable() {
                    @Override
                    public void run() {
                        launchGame();
                    }
                }
            );
        }
    }

    private void requestInstallPermissionOrInstall() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            !getPackageManager().canRequestPackageInstalls()) {
            try {
                Intent settingsIntent = new Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:" + getPackageName())
                );
                startActivityForResult(
                    settingsIntent,
                    REQUEST_UNKNOWN_SOURCES
                );
                return;
            } catch (Exception exception) {
                Log.e(TAG, "Unable to open unknown-source settings.", exception);
                launchGame();
                return;
            }
        }
        installPendingApk();
    }

    @Override
    protected void onActivityResult(
        int requestCode,
        int resultCode,
        Intent data
    ) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQUEST_UNKNOWN_SOURCES) {
            return;
        }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O ||
            getPackageManager().canRequestPackageInstalls()) {
            installPendingApk();
        } else {
            launchGame();
        }
    }

    private void installPendingApk() {
        if (pendingApk == null || !pendingApk.isFile()) {
            launchGame();
            return;
        }

        PackageInstaller installer = getPackageManager().getPackageInstaller();
        PackageInstaller.Session session = null;
        try {
            PackageInstaller.SessionParams parameters =
                new PackageInstaller.SessionParams(
                    PackageInstaller.SessionParams.MODE_FULL_INSTALL
                );
            parameters.setAppPackageName(getPackageName());
            parameters.setSize(pendingApk.length());
            int sessionId = installer.createSession(parameters);
            session = installer.openSession(sessionId);
            try (
                InputStream input = new BufferedInputStream(
                    new FileInputStream(pendingApk)
                );
                OutputStream output = session.openWrite(
                    "base.apk",
                    0,
                    pendingApk.length()
                )
            ) {
                byte[] buffer = new byte[128 * 1024];
                int count;
                while ((count = input.read(buffer)) >= 0) {
                    output.write(buffer, 0, count);
                }
                output.flush();
                session.fsync(output);
            }

            Intent statusIntent = new Intent(this, UpdateInstallReceiver.class);
            statusIntent.setAction(INSTALL_ACTION);
            statusIntent.putExtra(
                PackageInstaller.EXTRA_SESSION_ID,
                sessionId
            );
            int flags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                flags |= PendingIntent.FLAG_MUTABLE;
            }
            PendingIntent status = PendingIntent.getBroadcast(
                this,
                sessionId,
                statusIntent,
                flags
            );
            IntentSender sender = status.getIntentSender();
            session.commit(sender);
        } catch (Exception exception) {
            Log.e(TAG, "Unable to commit update session.", exception);
            launchGame();
        } finally {
            if (session != null) {
                session.close();
            }
        }
    }

    private UpdateArtifact parseAndroidArtifact(JSONObject manifest)
        throws Exception {
        if (manifest.getInt("schema_version") != 1) {
            throw new IllegalArgumentException("Unsupported update schema.");
        }
        long versionCode = manifest.getLong("version_code");
        JSONArray artifacts = manifest.getJSONArray("artifacts");
        UpdateArtifact selected = null;
        for (int index = 0; index < artifacts.length(); index++) {
            JSONObject candidate = artifacts.getJSONObject(index);
            if (!"android".equals(candidate.getString("platform"))) {
                continue;
            }
            if (selected != null) {
                throw new IllegalArgumentException(
                    "Duplicate Android artifacts."
                );
            }
            selected = new UpdateArtifact(
                versionCode,
                candidate.getString("filename"),
                candidate.getString("url"),
                candidate.getLong("size"),
                candidate.getString("sha256")
            );
        }
        if (selected == null) {
            throw new IllegalArgumentException("Android artifact missing.");
        }
        selected.validate();
        return selected;
    }

    private void downloadApk(UpdateArtifact artifact, File destination)
        throws Exception {
        if (artifact.size > MAX_APK_BYTES) {
            throw new IllegalArgumentException("APK exceeds size limit.");
        }
        HttpURLConnection connection = openConnection(
            artifact.url,
            DOWNLOAD_CONNECT_TIMEOUT_MS,
            DOWNLOAD_READ_TIMEOUT_MS
        );
        try {
            int status = connection.getResponseCode();
            if (status < 200 || status >= 300) {
                throw new IllegalStateException(
                    "APK HTTP status " + status
                );
            }
            assertFinalConnectionIsHttps(connection);
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            long total = 0;
            try (
                InputStream input = new BufferedInputStream(
                    connection.getInputStream()
                );
                OutputStream output = new BufferedOutputStream(
                    new FileOutputStream(destination)
                )
            ) {
                byte[] buffer = new byte[128 * 1024];
                int count;
                while ((count = input.read(buffer)) >= 0) {
                    total += count;
                    if (total > artifact.size || total > MAX_APK_BYTES) {
                        throw new IllegalStateException(
                            "APK download exceeds declared size."
                        );
                    }
                    output.write(buffer, 0, count);
                    digest.update(buffer, 0, count);
                }
            }
            if (total != artifact.size) {
                throw new IllegalStateException(
                    "APK size mismatch: " + total + " != " + artifact.size
                );
            }
            String actualHash = toHex(digest.digest());
            if (!actualHash.equals(artifact.sha256)) {
                throw new SecurityException("APK SHA-256 mismatch.");
            }
        } finally {
            connection.disconnect();
        }
    }

    private void validateDownloadedApk(
        UpdateArtifact artifact,
        File apk
    ) throws Exception {
        PackageManager manager = getPackageManager();
        int flags = Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
            ? PackageManager.GET_SIGNING_CERTIFICATES
            : PackageManager.GET_SIGNATURES;
        PackageInfo candidate = manager.getPackageArchiveInfo(
            apk.getAbsolutePath(),
            flags
        );
        PackageInfo installed = manager.getPackageInfo(getPackageName(), flags);
        if (candidate == null) {
            throw new SecurityException("Downloaded APK cannot be parsed.");
        }
        if (!getPackageName().equals(candidate.packageName)) {
            throw new SecurityException("Downloaded APK package mismatch.");
        }
        long candidateCode = getVersionCode(candidate);
        if (candidateCode != artifact.versionCode ||
            candidateCode <= getVersionCode(installed)) {
            throw new SecurityException("Downloaded APK version mismatch.");
        }
        String[] currentCertificates = certificateDigests(installed);
        String[] candidateCertificates = certificateDigests(candidate);
        if (!Arrays.equals(currentCertificates, candidateCertificates)) {
            throw new SecurityException(
                "Downloaded APK signing certificate mismatch."
            );
        }
    }

    private static String[] certificateDigests(PackageInfo info)
        throws Exception {
        Signature[] signatures;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            if (info.signingInfo == null) {
                throw new SecurityException("SigningInfo missing.");
            }
            signatures = info.signingInfo.hasMultipleSigners()
                ? info.signingInfo.getApkContentsSigners()
                : info.signingInfo.getSigningCertificateHistory();
        } else {
            signatures = info.signatures;
        }
        if (signatures == null || signatures.length == 0) {
            throw new SecurityException("APK signatures missing.");
        }
        String[] digests = new String[signatures.length];
        MessageDigest sha256 = MessageDigest.getInstance("SHA-256");
        for (int index = 0; index < signatures.length; index++) {
            digests[index] = toHex(sha256.digest(signatures[index].toByteArray()));
            sha256.reset();
        }
        Arrays.sort(digests);
        return digests;
    }

    private long getInstalledVersionCode() throws Exception {
        PackageInfo info = getPackageManager().getPackageInfo(
            getPackageName(),
            0
        );
        return getVersionCode(info);
    }

    private static long getVersionCode(PackageInfo info) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            return info.getLongVersionCode();
        }
        return info.versionCode;
    }

    private byte[] downloadBytes(
        String url,
        int connectTimeout,
        int readTimeout,
        int maximumBytes
    ) throws Exception {
        HttpURLConnection connection = openConnection(
            url,
            connectTimeout,
            readTimeout
        );
        try {
            int status = connection.getResponseCode();
            if (status < 200 || status >= 300) {
                throw new IllegalStateException(
                    "Manifest HTTP status " + status
                );
            }
            assertFinalConnectionIsHttps(connection);
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            try (
                InputStream input = new BufferedInputStream(
                    connection.getInputStream()
                )
            ) {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = input.read(buffer)) >= 0) {
                    if (output.size() + count > maximumBytes) {
                        throw new IllegalStateException(
                            "Manifest exceeds size limit."
                        );
                    }
                    output.write(buffer, 0, count);
                }
            }
            return output.toByteArray();
        } finally {
            connection.disconnect();
        }
    }

    private static HttpURLConnection openConnection(
        String rawUrl,
        int connectTimeout,
        int readTimeout
    ) throws Exception {
        URL url = new URL(rawUrl);
        if (!"https".equalsIgnoreCase(url.getProtocol())) {
            throw new SecurityException("Only HTTPS update URLs are allowed.");
        }
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        connection.setConnectTimeout(connectTimeout);
        connection.setReadTimeout(readTimeout);
        connection.setInstanceFollowRedirects(true);
        connection.setUseCaches(false);
        connection.setRequestProperty(
            "User-Agent",
            "MClonePC-Android-Updater/1"
        );
        return connection;
    }

    private static void assertFinalConnectionIsHttps(
        HttpURLConnection connection
    ) {
        if (!"https".equalsIgnoreCase(connection.getURL().getProtocol())) {
            throw new SecurityException(
                "An update redirect left the HTTPS transport."
            );
        }
    }

    private void continueWithoutUpdate(String reason) {
        if (!startupDecisionMade.compareAndSet(false, true)) {
            return;
        }
        Log.i(TAG, "Starting installed game: " + reason);
        runOnUiThread(
            new Runnable() {
                @Override
                public void run() {
                    launchGame();
                }
            }
        );
    }

    private void launchGame() {
        if (isFinishing()) {
            return;
        }
        Intent game = new Intent(
            this,
            com.ansca.corona.CoronaActivity.class
        );
        game.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP);
        startActivity(game);
        finish();
    }

    private static void copyFile(File source, File destination)
        throws Exception {
        try (
            InputStream input = new BufferedInputStream(
                new FileInputStream(source)
            );
            OutputStream output = new BufferedOutputStream(
                new FileOutputStream(destination)
            )
        ) {
            byte[] buffer = new byte[128 * 1024];
            int count;
            while ((count = input.read(buffer)) >= 0) {
                output.write(buffer, 0, count);
            }
        }
    }

    private static void deleteIfPresent(File file) {
        if (file.exists() && !file.delete()) {
            Log.w(TAG, "Unable to delete " + file);
        }
    }

    private static String toHex(byte[] value) {
        StringBuilder result = new StringBuilder(value.length * 2);
        for (byte current : value) {
            result.append(
                String.format(Locale.US, "%02x", current & 0xff)
            );
        }
        return result.toString();
    }

    private static final class UpdateArtifact {
        final long versionCode;
        final String filename;
        final String url;
        final long size;
        final String sha256;

        UpdateArtifact(
            long versionCode,
            String filename,
            String url,
            long size,
            String sha256
        ) {
            this.versionCode = versionCode;
            this.filename = filename;
            this.url = url;
            this.size = size;
            this.sha256 = sha256.toLowerCase(Locale.US);
        }

        void validate() {
            if (versionCode < 1) {
                throw new IllegalArgumentException("Invalid versionCode.");
            }
            if (filename.length() == 0 ||
                !filename.equals(new File(filename).getName())) {
                throw new IllegalArgumentException("Unsafe APK filename.");
            }
            if (!url.startsWith("https://")) {
                throw new IllegalArgumentException("APK URL is not HTTPS.");
            }
            if (size < 1 || size > MAX_APK_BYTES) {
                throw new IllegalArgumentException("Invalid APK size.");
            }
            if (!sha256.matches("[0-9a-f]{64}")) {
                throw new IllegalArgumentException("Invalid APK SHA-256.");
            }
        }
    }
}
