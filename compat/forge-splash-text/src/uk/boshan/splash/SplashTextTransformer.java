package uk.boshan.splash;

import net.minecraft.launchwrapper.IClassTransformer;
import org.objectweb.asm.*;

/** GregAPI writes progress messages by reflection, bypassing Forge's character filter. */
public final class SplashTextTransformer implements IClassTransformer {
    public byte[] transform(String name, String transformedName, byte[] input) {
        if (!"gregapi.util.UT$LoadingBar".equals(transformedName)) return input;
        final int[] changed = {0};
        ClassWriter output = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        new ClassReader(input).accept(new ClassVisitor(Opcodes.ASM5, output) {
            public MethodVisitor visitMethod(int access, String name, String desc, String signature, String[] exceptions) {
                MethodVisitor original = super.visitMethod(access, name, desc, signature, exceptions);
                if (!name.equals("step") || !desc.equals("(Ljava/lang/Object;)Z")) return original;
                return new MethodVisitor(Opcodes.ASM5, original) {
                    public void visitMethodInsn(int opcode, String owner, String name, String desc, boolean itf) {
                        if (opcode == Opcodes.INVOKEVIRTUAL && owner.equals("java/lang/reflect/Field")
                                && name.equals("set") && desc.equals("(Ljava/lang/Object;Ljava/lang/Object;)V")) {
                            // Keep the original no-bar short circuit, null handling and try/catch.
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, "uk/boshan/splash/SplashText", "safe", "(Ljava/lang/Object;)Ljava/lang/Object;", false);
                            changed[0]++;
                        }
                        super.visitMethodInsn(opcode, owner, name, desc, itf);
                    }
                };
            }
        }, 0);
        if (changed[0] != 1) throw new IllegalStateException("Unexpected GregAPI loading bar implementation");
        System.out.println("[MojinSplashFix] GregAPI loading text compatibility enabled");
        return output.toByteArray();
    }
}
