package plugin.mclonecloud;

final class AccessTokenCache {
    interface Clock {
        long now();
    }

    private final long lifetimeMs;
    private final Clock clock;
    private String token;
    private long expiresAtMs;

    AccessTokenCache(long lifetime) {
        this(lifetime, new Clock() {
            @Override
            public long now() {
                return System.currentTimeMillis();
            }
        });
    }

    AccessTokenCache(long lifetime, Clock clockValue) {
        if (lifetime <= 0L || clockValue == null) {
            throw new IllegalArgumentException("Invalid token cache.");
        }
        lifetimeMs = lifetime;
        clock = clockValue;
    }

    synchronized String get() {
        if (token == null || clock.now() >= expiresAtMs) {
            clear();
            return null;
        }
        return token;
    }

    synchronized void put(String value) {
        if (value == null || value.length() == 0) {
            throw new IllegalArgumentException("Invalid access token.");
        }
        token = value;
        expiresAtMs = clock.now() + lifetimeMs;
    }

    synchronized void clear() {
        token = null;
        expiresAtMs = 0L;
    }
}
