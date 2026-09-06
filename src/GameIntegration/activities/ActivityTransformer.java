package uk.boshan.activities;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.*;

public final class ActivityTransformer implements ClassFileTransformer {
    private static final String R = "uk/boshan/activities/ActivityRuntime";
    public byte[] transform(ClassLoader loader, String name, Class<?> redef, ProtectionDomain domain, byte[] bytes) {
        boolean common = name.equals("cpw/mods/fml/common/FMLCommonHandler") || name.equals("net/minecraftforge/fml/common/FMLCommonHandler") || name.equals("net/minecraftforge/event/ForgeEventFactory");
        boolean hooks = name.equals("net/minecraftforge/common/ForgeHooks");
        boolean bq = name.equals("betterquesting/questing/QuestInstance");
        boolean ftb = name.equals("dev/ftb/mods/ftbquests/quest/TeamData");
        boolean cloneEvent = name.equals("net/minecraftforge/event/entity/player/PlayerEvent$Clone");
        boolean gt6 = name.equals("gregapi/tileentity/machines/MultiTileEntityBasicMachine");
        if (!common && !hooks && !bq && !ftb && !cloneEvent && !gt6) return null;
        try {
            ClassReader reader = new ClassReader(bytes);
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    MethodVisitor target = super.visitMethod(access, method, desc, sig, exceptions);
                    final int first = (access & Opcodes.ACC_STATIC) == 0 ? 1 : 0;
                    final Type[] args = Type.getArgumentTypes(desc);
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        private void call(String m, String d) { super.visitMethodInsn(Opcodes.INVOKESTATIC, R, m, d, false); }
                        public void visitMethodInsn(int opcode,String owner,String called,String descriptor,boolean itf) {
                            boolean output=gt6 && method.equals("doActive") && called.equals("addStackToSlot") && descriptor.equals("(ILnet/minecraft/item/ItemStack;)Z");
                            if(output){super.visitInsn(Opcodes.DUP);super.visitVarInsn(Opcodes.ALOAD,0);call("beginMachineOutput","(Ljava/lang/Object;Ljava/lang/Object;)V");}
                            super.visitMethodInsn(opcode,owner,called,descriptor,itf);
                            if(output){super.visitInsn(Opcodes.DUP);call("finishMachineOutput","(Z)V");}
                        }
                        public void visitCode() {
                            super.visitCode();
                            if (bq && (method.equals("update") || method.equals("detect")) && args.length == 1) {
                                super.visitVarInsn(Opcodes.ALOAD, first); call("questContext", "(Ljava/lang/Object;)V");
                            }
                        }
                        public void visitInsn(int op) {
                            if(op==Opcodes.RETURN && cloneEvent && method.equals("<init>") && args.length==3){
                                super.visitVarInsn(Opcodes.ALOAD,1);super.visitVarInsn(Opcodes.ALOAD,2);call("clonePlayer","(Ljava/lang/Object;Ljava/lang/Object;)V");
                            }
                            if (op == Opcodes.RETURN && common && args.length > 0) {
                                if (method.equals("onPlayerPostTick")) {super.visitVarInsn(Opcodes.ALOAD,first);call("tick","(Ljava/lang/Object;)V");}
                                if (method.equals("firePlayerCraftingEvent") || method.equals("firePlayerSmeltedEvent")) {
                                    super.visitVarInsn(Opcodes.ALOAD,first);super.visitVarInsn(Opcodes.ALOAD,first+1);call("crafted","(Ljava/lang/Object;Ljava/lang/Object;)V");
                                }
                                if (method.equals("onPlayerClone") && args.length==3) {
                                    super.visitVarInsn(Opcodes.ALOAD,first);super.visitVarInsn(Opcodes.ALOAD,first+1);call("clonePlayer","(Ljava/lang/Object;Ljava/lang/Object;)V");
                                }
                                if (method.equals("firePlayerLoggedOut")) {super.visitVarInsn(Opcodes.ALOAD,first);call("logout","(Ljava/lang/Object;)V");}
                            }
                            if (op == Opcodes.RETURN && bq && method.equals("setComplete") && desc.equals("(Ljava/util/UUID;J)V")) {
                                super.visitVarInsn(Opcodes.ALOAD,0);super.visitVarInsn(Opcodes.ALOAD,1);super.visitVarInsn(Opcodes.LLOAD,2);call("bqComplete","(Ljava/lang/Object;Ljava/util/UUID;J)V");
                            }
                            if ((op == Opcodes.RETURN || op == Opcodes.ATHROW) && bq && (method.equals("update") || method.equals("detect")) && args.length == 1) call("clearQuestContext","()V");
                            if (op == Opcodes.IRETURN && ftb && method.equals("setCompleted") && desc.equals("(JLjava/util/Date;)Z")) {
                                super.visitInsn(Opcodes.DUP);super.visitVarInsn(Opcodes.ALOAD,0);super.visitVarInsn(Opcodes.LLOAD,1);super.visitVarInsn(Opcodes.ALOAD,3);call("ftbComplete","(ZLjava/lang/Object;JLjava/util/Date;)V");
                            }
                            if (op == Opcodes.IRETURN && hooks && method.equals("onLivingDeath") && args.length == 2) {
                                super.visitInsn(Opcodes.DUP);super.visitVarInsn(Opcodes.ALOAD,first);super.visitVarInsn(Opcodes.ALOAD,first+1);call("killed","(ZLjava/lang/Object;Ljava/lang/Object;)V");
                            }
                            super.visitInsn(op);
                        }
                    };
                }
            }, 0);
            System.out.println("[Mojin Activities] observing " + name);
            return writer.toByteArray();
        } catch (Throwable error) { ActivityRuntime.problem("hook:" + name, error); return null; }
    }
}
