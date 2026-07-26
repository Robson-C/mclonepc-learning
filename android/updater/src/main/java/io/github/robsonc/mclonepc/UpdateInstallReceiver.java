package io.github.robsonc.mclonepc;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.os.Build;
import android.os.SystemClock;
import android.util.Log;

public final class UpdateInstallReceiver extends BroadcastReceiver {
    private static final String TAG = "MClonePC.Update";

    @Override
    public void onReceive(Context context, Intent intent) {
        int status = intent.getIntExtra(
            PackageInstaller.EXTRA_STATUS,
            PackageInstaller.STATUS_FAILURE
        );
        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirmation;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                confirmation = intent.getParcelableExtra(
                    Intent.EXTRA_INTENT,
                    Intent.class
                );
            } else {
                @SuppressWarnings("deprecation")
                Intent legacy = intent.getParcelableExtra(Intent.EXTRA_INTENT);
                confirmation = legacy;
            }
            if (confirmation != null) {
                confirmation.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                context.startActivity(confirmation);
            }
            return;
        }

        if (status != PackageInstaller.STATUS_SUCCESS) {
            Log.e(
                TAG,
                "APK install failed: " + intent.getStringExtra(
                    PackageInstaller.EXTRA_STATUS_MESSAGE
                )
            );
        }
        scheduleGameRestart(context);
    }

    private void scheduleGameRestart(Context context) {
        Intent restart = new Intent(context, UpdateGateActivity.class);
        restart.putExtra(UpdateGateActivity.EXTRA_SKIP_CHECK_ONCE, true);
        restart.addFlags(
            Intent.FLAG_ACTIVITY_NEW_TASK |
            Intent.FLAG_ACTIVITY_CLEAR_TASK
        );
        int flags =
            PendingIntent.FLAG_CANCEL_CURRENT |
            PendingIntent.FLAG_ONE_SHOT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= PendingIntent.FLAG_IMMUTABLE;
        }
        PendingIntent pending = PendingIntent.getActivity(
            context,
            9182,
            restart,
            flags
        );
        AlarmManager alarm = (AlarmManager) context.getSystemService(
            Context.ALARM_SERVICE
        );
        if (alarm != null) {
            alarm.set(
                AlarmManager.ELAPSED_REALTIME,
                SystemClock.elapsedRealtime() + 700L,
                pending
            );
        } else {
            try {
                context.startActivity(restart);
            } catch (Exception exception) {
                Log.e(TAG, "Unable to restart after update.", exception);
            }
        }
    }
}
