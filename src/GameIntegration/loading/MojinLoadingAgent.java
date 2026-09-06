package uk.boshan.loading;

import java.lang.instrument.Instrumentation;
import java.lang.reflect.Method;
import java.lang.reflect.Field;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/** Read-only loading telemetry. No transformations, game hooks, listener replacement or network access. */
public final class MojinLoadingAgent {
    private static Method bars;
    private static boolean modern;
    private static long lastDiscovery;
    private static Instrumentation instrumentation;
    private static Class<?> minecraft;
    private static boolean minecraftReady;
    private static final Set<Class<?>> readable = new HashSet<Class<?>>();

    public static void premain(String ignored, final Instrumentation instrumentation) {
        try {
            MojinLoadingAgent.instrumentation = instrumentation;
            final String name = System.getProperty("mojin.loading.session", "");
            if (!name.matches("[a-f0-9]{32}")) return;
            final Path directory = Paths.get(".hub", "loading");
            Files.createDirectories(directory);
            Thread sampler = new Thread(new Runnable() {
                public void run() {
                    Path target = directory.resolve(name + ".json");
                    Path temporary = directory.resolve(name + ".tmp");
                    while (!Files.exists(directory.resolve(name + ".stop"))) {
                        try {
                            discover(instrumentation);
                            String snapshot;
                            try { snapshot = snapshot(); }
                            catch (Throwable unavailable) { snapshot = unknown(); }
                            String json = "{\"session\":\"" + name + "\"," + snapshot + "}";
                            Files.write(temporary, json.getBytes(StandardCharsets.UTF_8));
                            try { Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
                            catch (AtomicMoveNotSupportedException ex) { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING); }
                            Thread.sleep(350);
                        } catch (InterruptedException interrupted) { return; }
                        catch (Throwable unavailable) {
                            try { Thread.sleep(1000); } catch (InterruptedException interrupted) { return; }
                        }
                    }
                    try { Files.deleteIfExists(target); Files.deleteIfExists(temporary); } catch (Exception ignored) { }
                }
            }, "Mojin loading telemetry");
            sampler.setDaemon(true);
            sampler.setPriority(Thread.MIN_PRIORITY);
            sampler.start();
        } catch (Throwable unavailable) {
            // A presentation component must never prevent the game from starting.
            System.err.println("[Mojin] Loading telemetry unavailable; use the game window.");
        }
    }

    private static void discover(Instrumentation instrumentation) throws Exception {
        if ((bars != null && (!modern || minecraft != null)) || System.currentTimeMillis() - lastDiscovery < 1000) return;
        lastDiscovery = System.currentTimeMillis();
        for (Class<?> type : instrumentation.getAllLoadedClasses()) {
            String name = type.getName();
            if (name.equals("net.minecraft.client.Minecraft")) minecraft = type;
            boolean isModern = name.equals("net.minecraftforge.fml.loading.progress.StartupNotificationManager");
            if (bars == null && (isModern || name.equals("cpw.mods.fml.common.ProgressManager") || name.equals("net.minecraftforge.fml.common.ProgressManager"))) {
                // Forge 1.20 isolates loader classes in a named module. Export only this read API to our module.
                exportProgressPackage(instrumentation, type);
                bars = type.getMethod(isModern ? "getCurrentProgress" : "barIterator");
                modern = isModern;
            }
        }
    }

    private static void exportProgressPackage(Instrumentation instrumentation, Class<?> type) throws Exception {
        if (readable.contains(type)) return;
        try {
            Method getModule = Class.class.getMethod("getModule");
            Class<?> moduleClass = Class.forName("java.lang.Module");
            Object module = getModule.invoke(type), ours = getModule.invoke(MojinLoadingAgent.class);
            Map<String, Set<Object>> exports = Collections.singletonMap(type.getPackage().getName(), Collections.singleton(ours));
            Instrumentation.class.getMethod("redefineModule", moduleClass, Set.class, Map.class, Map.class, Set.class, Map.class)
                .invoke(instrumentation, module, Collections.emptySet(), exports, exports, Collections.emptySet(), Collections.emptyMap());
        } catch (NoSuchMethodException java8) { }
        readable.add(type);
    }

    static String snapshot() throws Exception {
        if (bars == null) return unknown();
        Object result = bars.invoke(null);
        Iterator<?> iterator = modern ? ((List<?>) result).iterator() : (Iterator<?>) result;
        Object active = null;
        String task = "", detail = "";
        // Read the loader's overall startup bar, never a nested mod/texture counter.
        // Forge/Cleanroom: Loader's seven-step Loading bar. Modern Forge: the Minecraft
        // reload aggregate (all registered reload tasks), published by ForgeLoadingOverlay.
        while (iterator.hasNext()) {
            Object candidate = iterator.next();
            String title = String.valueOf(call(candidate, modern ? "name" : "getTitle"));
            // Mod gathering/loading runs inside Minecraft's constructor, after class initialization.
            // Do not touch Minecraft's singleton before observing that real lifecycle signal.
            if (modern && title.startsWith("Mod ")) minecraftReady = true;
            String currentTask = taskFor(title);
            // Modern Forge pushes the newest nested meter to the front; legacy appends it.
            if (!currentTask.isEmpty() && (!modern || task.isEmpty())) {
                task = currentTask;
                // Legacy lifecycle messages are ModContainer.getName(), whereas modern labels
                // contain internal lifecycle identifiers. Never forward those modern labels.
                detail = !modern && task.equals("mods") ? displayName(call(candidate, "getMessage")) : "";
            }
            if (!title.equals(modern ? "Minecraft Progress" : "Loading")) continue;
            int total = number(candidate, modern ? "steps" : "getSteps");
            int completed = number(candidate, modern ? "current" : "getStep");
            if (active == null && total > 0 && completed >= 0 && completed <= total) {
                active = candidate;
            }
        }
        if (modern) {
            // Custom loading screens can leave Forge's published meter permanently at zero.
            // Prefer the underlying real task aggregate, shared by both renderers.
            String reload = minecraftReload(task, detail);
            if (reload != null) return reload;
        }
        if (active == null) return frame(task, detail, 0, 0);
        int total = number(active, modern ? "steps" : "getSteps"), completed = number(active, modern ? "current" : "getStep");
        if (total <= 0 || completed < 0 || completed > total) return frame(task, detail, 0, 0);
        return frame(task, detail, completed, total);
    }
    private static String minecraftReload(String task, String detail) throws Exception {
        if (!minecraftReady || minecraft == null) return null;
        // FancyMenu/Drippy replaces ForgeLoadingOverlay and does not publish Minecraft Progress.
        // Read the very same ReloadInstance aggregate from the active vanilla/custom overlay.
        exportProgressPackage(instrumentation, minecraft);
        Object client = minecraft.getMethod("m_91087_").invoke(null);
        if (client == null) return null;
        Object overlay = minecraft.getMethod("m_91265_").invoke(client);
        if (overlay == null) return null;
        for (Class<?> type = overlay.getClass(); type != null; type = type.getSuperclass()) {
            for (Field field : type.getDeclaredFields()) {
                if (!field.getType().getName().equals("net.minecraft.server.packs.resources.ReloadInstance")) continue;
                exportProgressPackage(instrumentation, type);
                exportProgressPackage(instrumentation, field.getType());
                field.setAccessible(true);
                Object reload = field.get(overlay);
                if (reload == null) continue;
                double progress = ((Number) field.getType().getMethod("m_7750_").invoke(reload)).doubleValue();
                if (Double.isNaN(progress) || progress < 0 || progress > 1) return null;
                return frame(task.isEmpty() ? "resources" : task, detail, (int)(progress * 10000), 10000);
            }
        }
        return null;
    }
    private static Object call(Object target, String method) throws Exception { return target.getClass().getMethod(method).invoke(target); }
    private static int number(Object target, String method) throws Exception { return ((Number) call(target, method)).intValue(); }
    private static String unknown() { return frame("", "", 0, 0); }
    private static String frame(String task, String detail, int completed, int total) {
        return "\"phase\":\"loading\",\"task\":" + quote(task) + ",\"detail\":" + quote(detail)
            + ",\"completed\":" + completed + ",\"total\":" + total;
    }
    private static String taskFor(String title) {
        String key = title.toLowerCase(Locale.ROOT).replaceAll("[ _-]", "");
        if (key.equals("modgather") || key.equals("modgathering")) return "mods-discovery";
        if (key.equals("modcomplete") || key.equals("loadcomplete")) return "mods-complete";
        if (key.equals("construction") || key.equals("constructingmods") || key.equals("preinitialization")
                || key.equals("initialization") || key.equals("postinitialization") || key.equals("modloading")) return "mods";
        if (key.equals("texturecreation") || key.equals("texturestitching") || key.equals("textureloading")) return "textures";
        if (key.equals("modelloader") || key.equals("modelbaking") || key.equals("modelregistry") || key.equals("models")) return "models";
        if (key.equals("soundloading") || key.equals("soundhandler")) return "sounds";
        if (key.equals("resourceloading") || key.equals("resourcereload") || key.equals("reloadingresources")) return "resources";
        if (key.equals("recipeloading") || key.equals("recipes")) return "recipes";
        return "";
    }
    private static String displayName(Object value) {
        if (!(value instanceof String)) return "";
        String name = ((String) value).trim();
        if (name.isEmpty() || name.length() > 80 || !name.matches("[\\p{L}\\p{N} '&+_()\\-]+")) return "";
        String lower = name.toLowerCase(Locale.ROOT);
        for (String internal : new String[]{"forge", "cleanroom", "fml", "mixin", "lwjgl", "opengl", "javaagent", "modlauncher"})
            if (lower.contains(internal)) return "";
        return name;
    }
    private static String quote(String input) {
        StringBuilder out = new StringBuilder("\"");
        for (int i = 0; i < Math.min(input.length(), 220); i++) {
            char c = input.charAt(i);
            if (c == '\\' || c == '"') out.append('\\').append(c);
            else if (c < 32) out.append(' ');
            else out.append(c);
        }
        return out.append('"').toString();
    }
}
