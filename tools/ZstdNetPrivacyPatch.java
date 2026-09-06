import java.nio.file.*;
import org.objectweb.asm.*;

/** Redact the logging helper only. Never change handshake bytes or forwarding. */
public final class ZstdNetPrivacyPatch {
    public static void main(String[] args) throws Exception {
        ClassReader reader = new ClassReader(Files.readAllBytes(Paths.get(args[0])));
        ClassWriter writer = new ClassWriter(0);
        final int[] replaced = {0};
        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            public MethodVisitor visitMethod(int access, String name, String descriptor, String signature, String[] exceptions) {
                MethodVisitor target = super.visitMethod(access, name, descriptor, signature, exceptions);
                if (name.equals("sanitizeHandshakeHost") && descriptor.equals("(Ljava/lang/String;)Ljava/lang/String;")) {
                    replaced[0]++;
                    target.visitCode();target.visitLdcInsn("[handshake-redacted]");target.visitInsn(Opcodes.ARETURN);
                    target.visitMaxs(1,1);target.visitEnd();return null;
                }
                return target;
            }
        },0);
        if (replaced[0] != 1) throw new IllegalStateException("Unexpected ZstdNet logging helper; patch aborted");
        Files.write(Paths.get(args[1]),writer.toByteArray());
    }
}
