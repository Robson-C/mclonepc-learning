package io.github.robsonc.mclonepc;

import java.util.concurrent.atomic.AtomicBoolean;

final class ProcessUpdateCheckGuard {
    private static final AtomicBoolean CLAIMED = new AtomicBoolean(false);

    private ProcessUpdateCheckGuard() {
    }

    static boolean claim() {
        return CLAIMED.compareAndSet(false, true);
    }
}
