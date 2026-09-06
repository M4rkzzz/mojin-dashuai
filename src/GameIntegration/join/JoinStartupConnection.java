package uk.boshan.join;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.security.ProtectionDomain;
import org.objectweb.asm.*;

/** Keep the ordinary connection GUI responsive; Forge's startup status wait runs on the caller thread. */
public final class JoinStartupConnection {
    public static void install(Instrumentation instrumentation) {
        instrumentation.addTransformer(new ClassFileTransformer() {
            public byte[] transform(ClassLoader loader, String name, Class<?> type, ProtectionDomain domain, byte[] bytes) {
                if (!"cpw/mods/fml/client/FMLClientHandler".equals(name)
                        && !"net/minecraftforge/fml/client/FMLClientHandler".equals(name)) return null;
                return rewrite(bytes);
            }
        }, false);
    }

    public static byte[] rewrite(byte[] bytes) {
        ClassReader reader = new ClassReader(bytes);
        final ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        final int[] found = {0};
        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            public MethodVisitor visitMethod(int access, String name, String descriptor, String signature, String[] exceptions) {
                MethodVisitor method = super.visitMethod(access, name, descriptor, signature, exceptions);
                if (!name.equals("connectToServerAtStartup") || !descriptor.equals("(Ljava/lang/String;I)V")) return method;
                found[0]++;
                method.visitCode();
                method.visitVarInsn(Opcodes.ALOAD, 0);
                method.visitVarInsn(Opcodes.ALOAD, 1);
                method.visitVarInsn(Opcodes.ILOAD, 2);
                method.visitMethodInsn(Opcodes.INVOKESTATIC, "uk/boshan/join/JoinStartupConnection", "connect", "(Ljava/lang/Object;Ljava/lang/String;I)V", false);
                method.visitInsn(Opcodes.RETURN);
                method.visitMaxs(3, 3);
                method.visitEnd();
                return null;
            }
        }, 0);
        if (found[0] != 1) throw new IllegalStateException("Unsupported Forge startup connection method");
        System.out.println("[Mojin Join] responsive startup connection installed");
        return writer.toByteArray();
    }

    public static void connect(Object handler, String host, int port) {
        try {
            Class<?> type = handler.getClass();
            type.getMethod("setupServerList").invoke(handler);
            Method connect = null;
            for (Method method : type.getMethods()) {
                if (method.getName().equals("connectToServer") && method.getParameterTypes().length == 2) { connect = method; break; }
            }
            if (connect == null) throw new NoSuchMethodException("connectToServer");
            Class<?> serverType = connect.getParameterTypes()[1];
            Object server;
            try {
                Constructor<?> constructor = serverType.getConstructor(String.class, String.class, boolean.class);
                server = constructor.newInstance("Mojin", host + ":" + port, false);
            } catch (NoSuchMethodException legacy) {
                server = serverType.getConstructor(String.class, String.class).newInstance("Mojin", host + ":" + port);
            }
            // Null previous screen returns to the normal menu on cancel. The original
            // GuiConnecting handles DNS and sockets in its own connector thread.
            connect.invoke(handler, null, server);
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException("Unable to open the game connection screen", error);
        }
    }
}
