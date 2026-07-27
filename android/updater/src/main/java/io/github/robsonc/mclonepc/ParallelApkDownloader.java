package io.github.robsonc.mclonepc;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

final class ParallelApkDownloader {
    private static final int PART_COUNT = 4;
    private static final long MINIMUM_PARALLEL_BYTES = 8L * 1024L * 1024L;
    private static final int MAXIMUM_REDIRECTS = 5;

    private ParallelApkDownloader() {
    }

    static boolean download(
        String rawUrl,
        long expectedSize,
        File destination,
        int connectTimeout,
        int readTimeout
    ) throws Exception {
        if (expectedSize < MINIMUM_PARALLEL_BYTES) {
            return false;
        }
        if (!supportsRanges(
            rawUrl,
            expectedSize,
            connectTimeout,
            readTimeout
        )) {
            return false;
        }

        List<RangePart> ranges = createRanges(expectedSize, PART_COUNT);
        List<File> partFiles = new ArrayList<File>();
        for (int index = 0; index < ranges.size(); index++) {
            partFiles.add(new File(
                destination.getParentFile(),
                destination.getName() + ".part" + index
            ));
        }

        ExecutorService executor =
            Executors.newFixedThreadPool(ranges.size());
        List<Future<Void>> futures = new ArrayList<Future<Void>>();
        try {
            for (int index = 0; index < ranges.size(); index++) {
                final RangePart range = ranges.get(index);
                final File partFile = partFiles.get(index);
                deleteIfPresent(partFile);
                futures.add(executor.submit(new Callable<Void>() {
                    @Override
                    public Void call() throws Exception {
                        downloadPartWithRetry(
                            rawUrl,
                            range,
                            expectedSize,
                            partFile,
                            connectTimeout,
                            readTimeout
                        );
                        return null;
                    }
                }));
            }
            for (Future<Void> future : futures) {
                future.get();
            }
            assemble(partFiles, expectedSize, destination);
            return true;
        } finally {
            for (Future<Void> future : futures) {
                if (!future.isDone()) {
                    future.cancel(true);
                }
            }
            executor.shutdownNow();
            executor.awaitTermination(5L, TimeUnit.SECONDS);
            for (File partFile : partFiles) {
                deleteQuietly(partFile);
            }
        }
    }

    static List<RangePart> createRanges(long size, int count) {
        if (size <= 0L || count <= 0 || size < count) {
            throw new IllegalArgumentException("Invalid range plan.");
        }
        List<RangePart> result = new ArrayList<RangePart>();
        long baseLength = size / count;
        long remainder = size % count;
        long start = 0L;
        for (int index = 0; index < count; index++) {
            long length = baseLength + (index < remainder ? 1L : 0L);
            long end = start + length - 1L;
            result.add(new RangePart(start, end));
            start = end + 1L;
        }
        return result;
    }

    private static boolean supportsRanges(
        String rawUrl,
        long expectedSize,
        int connectTimeout,
        int readTimeout
    ) throws Exception {
        HttpURLConnection connection = openRangeConnection(
            rawUrl,
            0L,
            0L,
            connectTimeout,
            readTimeout
        );
        try {
            if (
                connection.getResponseCode() !=
                    HttpURLConnection.HTTP_PARTIAL ||
                !contentRangeMatches(
                    connection.getHeaderField("Content-Range"),
                    0L,
                    0L,
                    expectedSize
                )
            ) {
                return false;
            }
            try (
                InputStream input = new BufferedInputStream(
                    connection.getInputStream()
                )
            ) {
                if (input.read() < 0 || input.read() >= 0) {
                    throw new IllegalStateException(
                        "Invalid range probe response."
                    );
                }
            }
            return true;
        } finally {
            connection.disconnect();
        }
    }

    private static void downloadPartWithRetry(
        String rawUrl,
        RangePart range,
        long expectedSize,
        File destination,
        int connectTimeout,
        int readTimeout
    ) throws Exception {
        Exception failure = null;
        for (int attempt = 0; attempt < 2; attempt++) {
            deleteIfPresent(destination);
            try {
                downloadPart(
                    rawUrl,
                    range,
                    expectedSize,
                    destination,
                    connectTimeout,
                    readTimeout
                );
                return;
            } catch (Exception exception) {
                failure = exception;
            }
        }
        throw failure;
    }

    private static void downloadPart(
        String rawUrl,
        RangePart range,
        long expectedSize,
        File destination,
        int connectTimeout,
        int readTimeout
    ) throws Exception {
        HttpURLConnection connection = openRangeConnection(
            rawUrl,
            range.start,
            range.end,
            connectTimeout,
            readTimeout
        );
        try {
            int status = connection.getResponseCode();
            if (
                status != HttpURLConnection.HTTP_PARTIAL ||
                !contentRangeMatches(
                    connection.getHeaderField("Content-Range"),
                    range.start,
                    range.end,
                    expectedSize
                )
            ) {
                throw new IllegalStateException(
                    "APK range response is inconsistent."
                );
            }
            long total = 0L;
            try (
                InputStream input = new BufferedInputStream(
                    connection.getInputStream(),
                    256 * 1024
                );
                OutputStream output = new BufferedOutputStream(
                    new FileOutputStream(destination),
                    256 * 1024
                )
            ) {
                byte[] buffer = new byte[256 * 1024];
                int count;
                while ((count = input.read(buffer)) >= 0) {
                    total += count;
                    if (total > range.length()) {
                        throw new IllegalStateException(
                            "APK range exceeds declared length."
                        );
                    }
                    output.write(buffer, 0, count);
                }
            }
            if (total != range.length()) {
                throw new IllegalStateException(
                    "APK range length mismatch."
                );
            }
        } finally {
            connection.disconnect();
        }
    }

    static void assemble(
        List<File> partFiles,
        long expectedSize,
        File destination
    ) throws Exception {
        deleteIfPresent(destination);
        long total = 0L;
        try (
            OutputStream output = new BufferedOutputStream(
                new FileOutputStream(destination),
                256 * 1024
            )
        ) {
            byte[] buffer = new byte[256 * 1024];
            for (File partFile : partFiles) {
                try (
                    InputStream input = new BufferedInputStream(
                        new FileInputStream(partFile),
                        256 * 1024
                    )
                ) {
                    int count;
                    while ((count = input.read(buffer)) >= 0) {
                        total += count;
                        if (total > expectedSize) {
                            throw new IllegalStateException(
                                "Assembled APK exceeds declared size."
                            );
                        }
                        output.write(buffer, 0, count);
                    }
                }
            }
        }
        if (total != expectedSize) {
            throw new IllegalStateException(
                "Assembled APK size mismatch."
            );
        }
    }

    private static HttpURLConnection openRangeConnection(
        String rawUrl,
        long start,
        long end,
        int connectTimeout,
        int readTimeout
    ) throws Exception {
        URL url = new URL(rawUrl);
        for (int redirects = 0; redirects <= MAXIMUM_REDIRECTS; redirects++) {
            requireHttps(url);
            HttpURLConnection connection =
                (HttpURLConnection) url.openConnection();
            connection.setConnectTimeout(connectTimeout);
            connection.setReadTimeout(readTimeout);
            connection.setInstanceFollowRedirects(false);
            connection.setUseCaches(false);
            connection.setRequestProperty(
                "User-Agent",
                "MClonePC-Android-Updater/1"
            );
            connection.setRequestProperty(
                "Range",
                "bytes=" + start + "-" + end
            );
            int status = connection.getResponseCode();
            if (
                status != HttpURLConnection.HTTP_MOVED_PERM &&
                status != HttpURLConnection.HTTP_MOVED_TEMP &&
                status != HttpURLConnection.HTTP_SEE_OTHER &&
                status != 307 &&
                status != 308
            ) {
                requireHttps(connection.getURL());
                return connection;
            }
            String location = connection.getHeaderField("Location");
            connection.disconnect();
            if (location == null || location.length() == 0) {
                throw new IllegalStateException(
                    "APK redirect has no destination."
                );
            }
            url = new URL(url, location);
        }
        throw new IllegalStateException("Too many APK redirects.");
    }

    static boolean contentRangeMatches(
        String value,
        long start,
        long end,
        long total
    ) {
        return (
            "bytes " + start + "-" + end + "/" + total
        ).equals(value);
    }

    private static void requireHttps(URL url) {
        if (!"https".equalsIgnoreCase(url.getProtocol())) {
            throw new SecurityException(
                "Only HTTPS update URLs are allowed."
            );
        }
    }

    private static void deleteIfPresent(File file) {
        if (file.exists() && !file.delete()) {
            throw new IllegalStateException(
                "Unable to remove stale update file."
            );
        }
    }

    private static void deleteQuietly(File file) {
        if (file.exists()) {
            file.delete();
        }
    }

    static final class RangePart {
        final long start;
        final long end;

        RangePart(long startValue, long endValue) {
            start = startValue;
            end = endValue;
        }

        long length() {
            return end - start + 1L;
        }
    }
}
