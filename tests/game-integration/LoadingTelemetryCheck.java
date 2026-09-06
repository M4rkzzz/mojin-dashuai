import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/** Runs the real pinned loader progress API with the actual -javaagent, without starting Minecraft. */
public final class LoadingTelemetryCheck {
    public static void main(String[] args) throws Exception {
        Class<?> manager = Class.forName(args[0]);
        boolean modern = args[0].endsWith("StartupNotificationManager");
        Object bar = addBar(manager, modern, modern ? "Minecraft Progress" : "Loading", 8, 3, "");
        // A mod callback at its last step is still real work, not overall completion.
        Object nested = addBar(manager, modern, modern ? "Mod Loading" : "PreInitialization", 2, 2,
            modern ? "net.internal.LifecycleEvent" : "GregTech 6");
        String session = System.getProperty("mojin.loading.session");
        Path status = Paths.get(".hub", "loading", session + ".json");
        awaitFrame(status, "\"completed\":3,\"total\":8", "\"task\":\"mods\"", modern ? "\"detail\":\"\"" : "\"detail\":\"GregTech 6\"");
        Thread.sleep(800);
        String unchanged = new String(Files.readAllBytes(status), StandardCharsets.UTF_8);
        if (!unchanged.contains("\"completed\":3,\"total\":8")) throw new AssertionError("Elapsed time changed the count");
        if (!modern) {
            message(nested, "net.minecraft.internal.SecretClass");
            awaitFrame(status, "\"task\":\"mods\"", "\"detail\":\"\"");
            message(nested, "Minecraft Forge");
            awaitFrame(status, "\"task\":\"mods\"", "\"detail\":\"\"");
            message(nested, "JourneyMap");
            awaitFrame(status, "\"task\":\"mods\"", "\"detail\":\"JourneyMap\"");
        }
        Object textures = addBar(manager, modern, "Texture stitching", 4, 1, "assets/private/file.png");
        awaitFrame(status, "\"task\":\"textures\"", "\"detail\":\"\"", "\"completed\":3,\"total\":8");
        removeBar(manager, modern, textures);
        removeBar(manager, modern, bar);
        awaitFrame(status, "\"task\":\"mods\"", "\"completed\":0,\"total\":0");
        Files.write(Paths.get(".hub", "loading", session + ".stop"), new byte[0]);
        Thread.sleep(600);
        if (Files.exists(status)) throw new AssertionError("Telemetry did not stop");
        System.out.println("PASS " + args[0] + " overall=3/8; live task/name; private detail filtered; task without count; no timer inflation; stopped");
    }
    private static Object addBar(Class<?> manager, boolean modern, String title, int total, int completed, String detail) throws Exception {
        if (modern) {
            Object bar = manager.getMethod("addProgressBar", String.class, int.class).invoke(null, title, total);
            bar.getClass().getMethod("setAbsolute", int.class).invoke(bar, completed);
            bar.getClass().getMethod("label", String.class).invoke(bar, detail);
            return bar;
        } else {
            Class<?> type = Class.forName(manager.getName() + "$ProgressBar");
            Constructor<?> constructor = type.getDeclaredConstructors()[0];
            constructor.setAccessible(true);
            Object[] values = new Object[constructor.getParameterTypes().length];
            values[0] = title; values[1] = total;
            Object bar = constructor.newInstance(values);
            Field step = type.getDeclaredField("step"); step.setAccessible(true); step.setInt(bar, completed);
            message(bar, detail);
            Field bars = manager.getDeclaredField("bars"); bars.setAccessible(true);
            ((List) bars.get(null)).add(bar);
            return bar;
        }
    }
    private static void message(Object bar, String value) throws Exception {
        Field field = bar.getClass().getDeclaredField("message");field.setAccessible(true);field.set(bar, value);
    }
    private static void removeBar(Class<?> manager, boolean modern, Object bar) throws Exception {
        if (modern) manager.getMethod("popBar", bar.getClass()).invoke(null, bar);
        else {Field field = manager.getDeclaredField("bars");field.setAccessible(true);((List) field.get(null)).remove(bar);}
    }
    private static void awaitFrame(Path status, String... expected) throws Exception {
        long deadline = System.currentTimeMillis() + 7000;
        String json = "";
        while (System.currentTimeMillis() < deadline) {
            if (Files.exists(status)) {
                json = new String(Files.readAllBytes(status), StandardCharsets.UTF_8);
                boolean matched = true;
                for (String value : expected) if (!json.contains(value)) matched = false;
                if (matched) return;
            }
            Thread.sleep(100);
        }
        throw new AssertionError("Missing actual loader telemetry " + Arrays.toString(expected) + ": " + json);
    }
}
