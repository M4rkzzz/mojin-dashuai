import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import uk.boshan.join.JoinStartupConnection;

/** Verify the old blocking body is replaced and both supported ServerData constructors work. */
public final class StartupConnectionCheck {
    public static final class LegacyData {
        public final String address;
        public LegacyData(String name, String address) { this.address = address; }
    }
    public static final class ModernData {
        public final String address;
        public ModernData(String name, String address, boolean lan) { this.address = address; }
    }
    public static final class LegacyHandler {
        public boolean setup;
        public String connected;
        public void setupServerList() { setup = true; }
        public void connectToServer(Object parent, LegacyData data) {
            if (!setup || parent != null) throw new AssertionError("Invalid connection setup");
            connected = data.address;
        }
        public void connectToServerAtStartup(String host, int port) { throw new AssertionError("Blocking startup path still used"); }
    }
    public static final class ModernHandler {
        public boolean setup;
        public String connected;
        public void setupServerList() { setup = true; }
        public void connectToServer(Object parent, ModernData data) {
            if (!setup || parent != null) throw new AssertionError("Invalid connection setup");
            connected = data.address;
        }
        public void connectToServerAtStartup(String host, int port) { throw new AssertionError("Blocking startup path still used"); }
    }
    private static void check(Class<?> original) throws Exception {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        try (InputStream input = original.getResourceAsStream("/" + original.getName().replace('.', '/') + ".class")) {
            byte[] block = new byte[4096]; int count;
            while ((count = input.read(block)) >= 0) buffer.write(block, 0, count);
        }
        final byte[] transformed = JoinStartupConnection.rewrite(buffer.toByteArray());
        Class<?> patched = new ClassLoader(StartupConnectionCheck.class.getClassLoader()) {
            Class<?> loadPatched() { return defineClass(null, transformed, 0, transformed.length); }
        }.loadPatched();
        Object handler = patched.getConstructor().newInstance();
        patched.getMethod("connectToServerAtStartup", String.class, int.class).invoke(handler, "test.invalid", 25565);
        if (!"test.invalid:25565".equals(patched.getField("connected").get(handler))) throw new AssertionError("Wrong destination");
    }
    public static void main(String[] arguments) throws Exception {
        check(LegacyHandler.class);
        check(ModernHandler.class);
        System.out.println("Startup connection replacement passed (legacy and modern)");
    }
}
