package io.github.robsonc.mclonepc;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.security.MessageDigest;
import java.util.Locale;

public final class ParallelApkDownloaderNetworkHarness {
    public static void main(String[] arguments) throws Exception {
        if (arguments.length != 4) {
            throw new IllegalArgumentException(
                "url size sha256 destination"
            );
        }
        String url = arguments[0];
        long size = Long.parseLong(arguments[1]);
        String expectedHash = arguments[2].toLowerCase(Locale.US);
        File destination = new File(arguments[3]);
        long started = System.nanoTime();
        boolean parallel = ParallelApkDownloader.download(
            url,
            size,
            destination,
            10_000,
            30_000
        );
        long elapsedMs = (System.nanoTime() - started) / 1_000_000L;
        if (!parallel) {
            throw new AssertionError("range transport unavailable");
        }
        if (destination.length() != size) {
            throw new AssertionError("size mismatch");
        }
        String actualHash = sha256(destination);
        if (!expectedHash.equals(actualHash)) {
            throw new AssertionError("hash mismatch");
        }
        System.out.println(
            "PARALLEL_APK_DOWNLOAD_OK elapsed_ms=" + elapsedMs +
            " bytes=" + size
        );
    }

    private static String sha256(File file) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (InputStream input = new FileInputStream(file)) {
            byte[] buffer = new byte[256 * 1024];
            int count;
            while ((count = input.read(buffer)) >= 0) {
                digest.update(buffer, 0, count);
            }
        }
        StringBuilder value = new StringBuilder();
        for (byte item : digest.digest()) {
            value.append(String.format(Locale.US, "%02x", item & 0xff));
        }
        return value.toString();
    }
}
