import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.zip.*;
import org.objectweb.asm.*;
import org.objectweb.asm.util.CheckClassAdapter;
import uk.boshan.activities.ActivityTransformer;

public final class ActivityClassCheck {
 public static void main(String[] args)throws Exception {
  for(int i=0;i<args.length;i+=2){
   String jar=args[i],name=args[i+1];byte[] input;
   try(ZipFile z=new ZipFile(jar);InputStream in=z.getInputStream(z.getEntry(name+".class"));ByteArrayOutputStream out=new ByteArrayOutputStream()){byte[] b=new byte[8192];int n;while((n=in.read(b))!=-1)out.write(b,0,n);input=out.toByteArray();}
   byte[] changed=new ActivityTransformer().transform(null,name,null,null,input);if(changed==null)throw new AssertionError("Missing transform "+name);
   new ClassReader(changed).accept(new CheckClassAdapter(new ClassWriter(0),true),0);
   Set<String> hooks=new TreeSet<String>();new ClassReader(changed).accept(new ClassVisitor(Opcodes.ASM9){public MethodVisitor visitMethod(int a,String m,String d,String s,String[] e){return new MethodVisitor(Opcodes.ASM9){public void visitMethodInsn(int op,String owner,String n,String desc,boolean itf){if(owner.equals("uk/boshan/activities/ActivityRuntime"))hooks.add(n);}};}},0);
   String[] expected=name.endsWith("MultiTileEntityBasicMachine")?new String[]{"beginMachineOutput","finishMachineOutput"}:name.endsWith("$Clone")?new String[]{"clonePlayer"}:name.endsWith("QuestInstance")?new String[]{"questContext","clearQuestContext","bqComplete"}:name.endsWith("TeamData")?new String[]{"ftbComplete"}:name.endsWith("ForgeHooks")?new String[]{"killed"}:new String[]{"tick","crafted"};
   for(String h:expected)if(!hooks.contains(h))throw new AssertionError(name+" missing "+h);
   System.out.println("ACTIVITY_CLASS_PASS "+Paths.get(jar).getFileName()+" "+name+" "+hooks);
  }
 }
}
