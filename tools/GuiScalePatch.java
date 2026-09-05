import java.nio.file.*;
import org.objectweb.asm.*;
import org.objectweb.asm.tree.*;

/** Reproducible patch of TipTheScales 1.12.2-1.0.4's two GUI bounds defects. */
public final class GuiScalePatch {
    public static void main(String[] args) throws Exception {
        ClassNode type=new ClassNode();new ClassReader(Files.readAllBytes(Path.of(args[0]))).accept(type,0);
        MethodNode method=type.methods.stream().filter(m->m.name.equals("func_148182_a")).findFirst().orElseThrow();
        int changed=0;
        for(AbstractInsnNode node:method.instructions.toArray()) {
            if(node instanceof MethodInsnNode call && call.owner.equals("java/awt/Toolkit") && call.name.equals("getDefaultToolkit")) {
                AbstractInsnNode screen=node.getNext(), dimension=screen.getNext();
                if(!(screen instanceof MethodInsnNode s) || !s.name.equals("getScreenSize") || !(dimension instanceof FieldInsnNode field)
                   || !field.owner.equals("java/awt/Dimension") || !(field.name.equals("width")||field.name.equals("height"))) throw new IllegalStateException("Unexpected upstream screen lookup");
                InsnList replacement=new InsnList();replacement.add(new VarInsnNode(Opcodes.ALOAD,1));
                replacement.add(new FieldInsnNode(Opcodes.GETFIELD,"net/minecraft/client/Minecraft",field.name.equals("width")?"field_71443_c":"field_71440_d","I"));
                method.instructions.insertBefore(node,replacement);method.instructions.remove(node);method.instructions.remove(screen);method.instructions.remove(dimension);changed++;
            }
            if(node instanceof MethodInsnNode call && call.owner.endsWith("/GuiNewOptionSlider") && call.name.equals("<init>")) {
                AbstractInsnNode subtraction=node.getPrevious(), one=subtraction.getPrevious();
                if(subtraction.getOpcode()!=Opcodes.ISUB||one.getOpcode()!=Opcodes.ICONST_1)throw new IllegalStateException("Unexpected upstream slider maximum");
                method.instructions.remove(subtraction);method.instructions.remove(one);changed++;
            }
        }
        if(changed!=3)throw new IllegalStateException("Expected exactly three fixes");
        ClassWriter output=new ClassWriter(ClassWriter.COMPUTE_MAXS);type.accept(output);
        Files.write(Path.of(args[1]),output.toByteArray());
    }
}
