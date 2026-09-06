import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/** Runs the real pinned loader progress API with the actual -javaagent, without starting Minecraft. */
public final class LoadingTelemetryCheck {
    public static void main(String[] args) throws Exception {
        Class<?> manager = Class.forName(args[0]);
        Object bar;
        if (args[0].endsWith("StartupNotificationManager")) {
            bar = manager.getMethod("addProgressBar", String.class, int.class).invoke(null, "Minecraft Progress", 8);
            bar.getClass().getMethod("setAbsolute", int.class).invoke(bar, 3);
            // A nested operation at 100% must not replace the overall 3/8 progress.
            Object nested = manager.getMethod("addProgressBar", String.class, int.class).invoke(null, "Nested texture task", 2);
            nested.getClass().getMethod("setAbsolute", int.class).invoke(nested, 2);
        } else {
            Class<?> type = Class.forName(args[0] + "$ProgressBar");
            Constructor<?> constructor = type.getDeclaredConstructors()[0];
            constructor.setAccessible(true);
            Object[] values = new Object[constructor.getParameterTypes().length];
            values[0] = "Loading"; values[1] = 8;
            bar = constructor.newInstance(values);
            Field step = type.getDeclaredField("step"); step.setAccessible(true); step.setInt(bar, 3);
            Field bars = manager.getDeclaredField("bars"); bars.setAccessible(true);
            ((List) bars.get(null)).add(bar);
            values[0] = "Nested texture task"; values[1] = 2;
            Object nested = constructor.newInstance(values);step.setInt(nested, 2);
            ((List) bars.get(null)).add(nested);
        }
        String session = System.getProperty("mojin.loading.session");
        Path status = Paths.get(".hub", "loading", session + ".json");
        long deadline = System.currentTimeMillis() + 7000;
        boolean matched = false;
        while (System.currentTimeMillis() < deadline) {
            if (Files.exists(status)) {
                String json = new String(Files.readAllBytes(status), StandardCharsets.UTF_8);
                if (json.contains("\"completed\":3,\"total\":8") && json.contains("\"phase\":\"loading\"")) { matched = true; break; }
            }
            Thread.sleep(100);
        }
        if (!matched) throw new AssertionError("Actual loader counter was not reported: " + args[0]);
        Thread.sleep(800);
        String unchanged = new String(Files.readAllBytes(status), StandardCharsets.UTF_8);
        if (!unchanged.contains("\"completed\":3,\"total\":8")) throw new AssertionError("Elapsed time changed the count");
        Files.write(Paths.get(".hub", "loading", session + ".stop"), new byte[0]);
        Thread.sleep(600);
        if (Files.exists(status)) throw new AssertionError("Telemetry did not stop");
        System.out.println("PASS " + args[0] + " actual=3/8; no timer inflation; stopped");
    }
}
