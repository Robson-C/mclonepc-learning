package plugin.mclonecloud;

import com.ansca.corona.CoronaEnvironment;
import com.ansca.corona.CoronaLua;
import com.ansca.corona.CoronaRuntime;
import com.ansca.corona.CoronaRuntimeListener;
import com.ansca.corona.CoronaRuntimeTask;
import com.naef.jnlua.JavaFunction;
import com.naef.jnlua.LuaState;
import com.naef.jnlua.NamedJavaFunction;

/**
 * Solar2D adapter for the Android Google Drive transport.
 *
 * Lua API:
 *   cloud.execute(action, payloadOrNil, listener)
 * Callback:
 *   listener({ name = "mcloneCloud", response = "<json>" })
 */
@SuppressWarnings({"unused", "WeakerAccess"})
public final class LuaLoader implements JavaFunction, CoronaRuntimeListener {
    private static final String EVENT_NAME = "mcloneCloud";

    public LuaLoader() {
        CoronaEnvironment.addRuntimeListener(this);
    }

    @Override
    public int invoke(LuaState state) {
        String libraryName = state.toString(1);
        state.register(
            libraryName,
            new NamedJavaFunction[] { new ExecuteWrapper() }
        );
        return 1;
    }

    private int execute(LuaState state) {
        final String action = state.checkString(1);
        final String payload = state.isString(2)
            ? state.toString(2)
            : null;
        if (!CoronaLua.isListener(state, 3, EVENT_NAME)) {
            throw new IllegalArgumentException(
                "mclonecloud.execute exige um listener no terceiro argumento."
            );
        }
        final int listener = CoronaLua.newRef(state, 3);
        AndroidCloudClient.getInstance().execute(
            action,
            payload,
            new AndroidCloudClient.Callback() {
                @Override
                public void complete(String responseJson) {
                    dispatch(listener, responseJson);
                }
            }
        );
        return 0;
    }

    private void dispatch(
        final int listener,
        final String responseJson
    ) {
        if (CoronaEnvironment.getCoronaActivity() == null) {
            return;
        }
        CoronaEnvironment.getCoronaActivity()
            .getRuntimeTaskDispatcher()
            .send(
                new CoronaRuntimeTask() {
                    @Override
                    public void executeUsing(CoronaRuntime runtime) {
                        LuaState state = runtime.getLuaState();
                        CoronaLua.newEvent(state, EVENT_NAME);
                        state.pushString(responseJson);
                        state.setField(-2, "response");
                        try {
                            CoronaLua.dispatchEvent(state, listener, 0);
                        } catch (Exception ignored) {
                        } finally {
                            CoronaLua.deleteRef(state, listener);
                        }
                    }
                }
            );
    }

    @Override
    public void onLoaded(CoronaRuntime runtime) {
    }

    @Override
    public void onStarted(CoronaRuntime runtime) {
    }

    @Override
    public void onSuspended(CoronaRuntime runtime) {
    }

    @Override
    public void onResumed(CoronaRuntime runtime) {
    }

    @Override
    public void onExiting(CoronaRuntime runtime) {
    }

    private final class ExecuteWrapper implements NamedJavaFunction {
        @Override
        public String getName() {
            return "execute";
        }

        @Override
        public int invoke(LuaState state) {
            return execute(state);
        }
    }
}
