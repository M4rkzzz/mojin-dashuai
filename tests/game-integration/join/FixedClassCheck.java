import java.io.*;
import java.lang.reflect.*;
import java.nio.file.*;
import java.util.zip.*;
import org.objectweb.asm.*;
import org.objectweb.asm.util.CheckClassAdapter;
import uk.boshan.join.JoinRuntime;

/** Checks injected control flow against bytes from the pinned game JARs. */
public final class FixedClassCheck {
    public static void main(String[] args) throws Exception {
        Field server = JoinRuntime.class.getDeclaredField("server"); server.setAccessible(true);
        for (int i = 0; i < args.length; i += 3) {
            String name = args[i], path = args[i + 1], entry = args[i + 2];
            server.setBoolean(null, name.endsWith("/NetworkManager") || name.endsWith("/Connection"));
            try (ZipFile zip = new ZipFile(path); InputStream in = zip.getInputStream(zip.getEntry(entry))) {
                ByteArrayOutputStream bytes = new ByteArrayOutputStream(); byte[] buffer = new byte[8192]; int n;
                while ((n = in.read(buffer)) >= 0) bytes.write(buffer, 0, n);
                byte[] transformed = JoinRuntime.transform(name, bytes.toByteArray());
                if (transformed == null) throw new AssertionError("No hook for " + name);
                new ClassReader(transformed).accept(new CheckClassAdapter(new ClassWriter(0), true), 0);
                System.out.println("FIXED_CLASS_PASS " + entry + " -> " + name);
                if (path.contains("Cleanroom") && name.endsWith("/C00Handshake")) {
                    byte[] promoted = bytes.toByteArray(); promoted[6] = 0; promoted[7] = 69;
                    byte[] recent = JoinRuntime.transform(name, promoted);
                    new ClassReader(recent).accept(new CheckClassAdapter(new ClassWriter(0), true), 0);
                    System.out.println("CLEANROOM_JAVA25_CLASS_PASS");
                }
            }
        }
    }
}
