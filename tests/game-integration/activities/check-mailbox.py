"""Failure-injection fixture for server inventory delivery; never opens a game or real player file."""
from pathlib import Path
import os,subprocess
root=Path(__file__).resolve().parents[3];out=root/'.local/activities/mailbox-fixture';src=out/'src';classes=out/'classes';classes.mkdir(parents=True,exist_ok=True)
sources={
'net/minecraft/nbt/NBTTagCompound.java':r'''
package net.minecraft.nbt;
import java.io.*;import java.util.*;
public class NBTTagCompound implements Serializable {
 public final Map<String,Object> values=new HashMap<String,Object>();
 public String getString(String k){Object v=values.get(k);return v instanceof String?(String)v:"";}
 public void setString(String k,String v){values.put(k,v);}public void setByte(String k,byte v){values.put(k,v);}public byte getByte(String k){return values.containsKey(k)?((Number)values.get(k)).byteValue():0;}
 public void setShort(String k,short v){values.put(k,v);}public short getShort(String k){return values.containsKey(k)?((Number)values.get(k)).shortValue():0;}
 public NBTTagCompound getCompoundTag(String k){return values.containsKey(k)?(NBTTagCompound)values.get(k):new NBTTagCompound();}
}''',
'net/minecraft/nbt/CompressedStreamTools.java':r'''
package net.minecraft.nbt;import java.io.*;
public class CompressedStreamTools { public static NBTTagCompound readCompressed(InputStream in)throws Exception{return (NBTTagCompound)new ObjectInputStream(in).readObject();} }
''',
'net/minecraft/item/Item.java':r'''
package net.minecraft.item;import java.util.*;public class Item implements java.io.Serializable {
 public final String name;public Item(String n){name=n;}public static final Registry itemRegistry=new Registry();
 public static class Registry {final Map<String,Item> map=new HashMap<String,Item>();public Object getObject(Object key){String k=String.valueOf(key);if(!map.containsKey(k))map.put(k,new Item(k));return map.get(k);}public String getNameForObject(Object i){return ((Item)i).name;}}
}''',
'net/minecraft/item/ItemStack.java':r'''
package net.minecraft.item;import net.minecraft.nbt.NBTTagCompound;
public class ItemStack implements java.io.Serializable {public final Item item;public int stackSize;public final int meta;public ItemStack(Item i,int n,int m){item=i;stackSize=n;meta=m;}public Item getItem(){return item;}public ItemStack copy(){return new ItemStack(item,stackSize,meta);}public NBTTagCompound writeToNBT(NBTTagCompound n){n.setShort("id",(short)50);n.setByte("Count",(byte)stackSize);n.setShort("Damage",(short)meta);return n;}}
''',
'net/minecraft/entity/player/InventoryPlayer.java':r'''
package net.minecraft.entity.player;import net.minecraft.item.*;
public class InventoryPlayer {public ItemStack[] items=new ItemStack[2];public int getSizeInventory(){return items.length;}public ItemStack getStackInSlot(int s){return items[s];}public void setInventorySlotContents(int s,ItemStack i){items[s]=i;}
 // A deliberately adversarial inventory returns true even for a partial insertion.
 public boolean addItemStackToInventory(ItemStack stack){boolean any=false;for(int i=0;i<items.length&&stack.stackSize>0;i++){if(items[i]!=null&&!items[i].item.name.equals(stack.item.name))continue;int room=64-(items[i]==null?0:items[i].stackSize);int n=Math.min(room,stack.stackSize);if(n>0){if(items[i]==null)items[i]=new ItemStack(stack.item,0,stack.meta);items[i].stackSize+=n;stack.stackSize-=n;any=true;}}return any;}
}''',
'net/minecraft/entity/player/EntityPlayerMP.java':r'''
package net.minecraft.entity.player;import java.util.*;import java.nio.file.*;import java.io.*;import net.minecraft.nbt.*;import net.minecraft.item.*;
public class EntityPlayerMP {public static Path directory;public static boolean failBefore,failAfter;public final UUID id;public InventoryPlayer inventory=new InventoryPlayer();public NBTTagCompound data=new NBTTagCompound();public EntityPlayerMP(UUID u){id=u;}public UUID getUniqueID(){return id;}public NBTTagCompound getEntityData(){return data;}
 public void save()throws Exception{if(failBefore){failBefore=false;throw new IOException("simulated failed save");}NBTTagCompound root=new NBTTagCompound();root.values.put("ForgeData",data);root.values.put("inventory",inventory.items);try(ObjectOutputStream o=new ObjectOutputStream(Files.newOutputStream(directory.resolve(id+".dat")))){o.writeObject(root);}if(failAfter){failAfter=false;throw new IOException("simulated lost save result");}}
 public void load()throws Exception{NBTTagCompound root;try(InputStream in=Files.newInputStream(directory.resolve(id+".dat"))){root=CompressedStreamTools.readCompressed(in);}data=root.getCompoundTag("ForgeData");inventory.items=(ItemStack[])root.values.get("inventory");}
}''',
'cpw/mods/fml/common/FMLCommonHandler.java':r'''
package cpw.mods.fml.common;import net.minecraft.entity.player.EntityPlayerMP;
public class FMLCommonHandler {public static FMLCommonHandler instance(){return new FMLCommonHandler();}public Server getMinecraftServerInstance(){return new Server();}public static class Server{public List getConfigurationManager(){return new List();}}public static class List{public void writePlayerData(EntityPlayerMP p)throws Exception{p.save();}}}
''',
'uk/boshan/activities/MailboxCheck.java':r'''
package uk.boshan.activities;import com.google.gson.*;import java.util.*;import java.nio.file.*;import net.minecraft.entity.player.*;import net.minecraft.item.*;
public class MailboxCheck {
 public static class Machine {public UUID mOwner;Machine(UUID owner){mOwner=owner;}}
 static void require(boolean ok,String msg){if(!ok)throw new AssertionError(msg);System.out.println("MAILBOX_PASS "+msg);}
 static ItemStack item(String name,int count){return new ItemStack((Item)Item.itemRegistry.getObject(name),count,0);}
 static JsonObject delivery(String id){return new JsonParser().parse("{\"id\":\""+id+"\",\"items\":[{\"id\":\"minecraft:torch\",\"meta\":0,\"count\":8,\"nbt\":\"{}\"}]}").getAsJsonObject();}
 public static void main(String[] args)throws Exception{
  ActivityRuntime.instance="m3e";ActivityRuntime.playerData=Paths.get(args[0]);Files.createDirectories(ActivityRuntime.playerData);EntityPlayerMP.directory=ActivityRuntime.playerData;
  ActivityRuntime.definition=new ActivityRuntime.Definition();ActivityRuntime.definition.id="m3e";ActivityRuntime.definition.trackedItems=new String[]{"minecraft:torch@0"};
  EntityPlayerMP p=new EntityPlayerMP(UUID.randomUUID());ActivityRuntime.crafted(p,item("minecraft:torch",4));JsonObject event=ActivityRuntime.events.poll();require(event!=null&&event.get("key").getAsString().equals("minecraft:torch@0"),"1.7 numeric NBT id resolves through actual registry identity");
  ActivityRuntime.events.clear();ActivityRuntime.definition.trackedControllers=new String[]{"minecraft:torch@0"};p.inventory.items[0]=item("minecraft:torch",1);
  ActivityRuntime.controllerSnapshot(p,ActivityRuntime.state(p));event=ActivityRuntime.events.poll();require(event!=null&&event.get("kind").getAsString().equals("snapshot")&&event.getAsJsonArray("facts").get(0).getAsString().equals("owned:minecraft:torch@0"),"controller presence is a fact, never production credit");
  ActivityRuntime.controllerSnapshot(p,ActivityRuntime.state(p));require(ActivityRuntime.events.isEmpty(),"repeated controller pickup cannot manufacture daily events");p.inventory.items[0]=null;
  ActivityRuntime.events.clear();ActivityRuntime.instance="vw";Machine machine=new Machine(p.id);
  ActivityRuntime.beginMachineOutput(item("minecraft:torch",4),machine);ActivityRuntime.finishMachineOutput(false);require(ActivityRuntime.events.isEmpty(),"blocked GT6 recipe output gives no production credit");
  ActivityRuntime.beginMachineOutput(item("minecraft:torch",4),machine);ActivityRuntime.finishMachineOutput(true);require(ActivityRuntime.events.poll()!=null,"completed GT6 recipe credits its online owner");
  ActivityRuntime.beginMachineOutput(item("minecraft:torch",4),new Machine(UUID.randomUUID()));ActivityRuntime.finishMachineOutput(true);require(ActivityRuntime.events.isEmpty(),"unowned or offline GT6 production gives no player credit");ActivityRuntime.instance="m3e";
  JsonObject d=delivery(UUID.randomUUID().toString());p.inventory.items[0]=item("minecraft:torch",62);p.inventory.items[1]=item("minecraft:stone",64);
  require(!ActivityRuntime.deliver(p,d)&&p.inventory.items[0].stackSize==62&&p.getEntityData().getString(ActivityRuntime.LEDGER).isEmpty(),"partial insertion fully rolls back when inventory is full");
  p.inventory.items[1]=null;require(ActivityRuntime.deliver(p,d),"eligible delivery persists inventory and receipt together");int count=p.inventory.items[0].stackSize+p.inventory.items[1].stackSize;
  require(count==70&&ActivityRuntime.deliver(p,d)&&p.inventory.items[0].stackSize+p.inventory.items[1].stackSize==70,"lost acknowledgement retries without duplicate items");
  EntityPlayerMP loaded=new EntityPlayerMP(p.id);loaded.load();require(ActivityRuntime.deliver(loaded,d)&&loaded.inventory.items[0].stackSize+loaded.inventory.items[1].stackSize==70,"server restart keeps delivery receipt with player inventory");
  EntityPlayerMP q=new EntityPlayerMP(UUID.randomUUID());JsonObject pending=delivery(UUID.randomUUID().toString());EntityPlayerMP.failBefore=true;try{ActivityRuntime.deliver(q,pending);throw new AssertionError("save should fail");}catch(java.lang.reflect.InvocationTargetException expected){}
  require(q.inventory.items[0].stackSize==8&&ActivityRuntime.deliver(q,pending)&&q.inventory.items[0].stackSize==8,"disk failure retries the same saved inventory without adding twice");
  EntityPlayerMP r=new EntityPlayerMP(UUID.randomUUID());EntityPlayerMP.failAfter=true;JsonObject after=delivery(UUID.randomUUID().toString());try{ActivityRuntime.deliver(r,after);throw new AssertionError("save result should fail");}catch(java.lang.reflect.InvocationTargetException expected){}
  require(ActivityRuntime.deliver(r,after)&&r.inventory.items[0].stackSize==8,"failure after save does not roll back a durable reward");
  EntityPlayerMP clone=new EntityPlayerMP(q.id);ActivityRuntime.clonePlayer(clone,q);require(clone.getEntityData().getString(ActivityRuntime.LEDGER).equals(q.getEntityData().getString(ActivityRuntime.LEDGER)),"death clone retains anti-duplicate receipts");
  ActivityRuntime.questContext(p);ActivityRuntime.questContext(q);ActivityRuntime.clearQuestContext();require(ActivityRuntime.context.get().peek()==p,"nested quest detection preserves acting player");ActivityRuntime.clearQuestContext();
  EntityPlayerMP tower=new EntityPlayerMP(UUID.randomUUID());tower.inventory.items=new ItemStack[3];
  JsonObject whole=new JsonParser().parse("{\"id\":\""+UUID.randomUUID()+"\",\"items\":[{\"id\":\"test:wall\",\"meta\":0,\"count\":64,\"nbt\":\"{}\"},{\"id\":\"test:wall\",\"meta\":0,\"count\":7,\"nbt\":\"{}\"},{\"id\":\"test:base\",\"meta\":0,\"count\":9,\"nbt\":\"{}\"},{\"id\":\"test:core\",\"meta\":0,\"count\":1,\"nbt\":\"{}\"}]}").getAsJsonObject();
  require(!ActivityRuntime.deliver(tower,whole)&&Arrays.stream(tower.inventory.items).allMatch(Objects::isNull),"full 81-block set rolls back when only its final core does not fit");
  tower.inventory.items=new ItemStack[4];require(ActivityRuntime.deliver(tower,whole)&&Arrays.stream(tower.inventory.items).mapToInt(i->i.stackSize).sum()==81,"full 81-block set including core is delivered together");
  EntityPlayerMP again=new EntityPlayerMP(tower.id);again.load();require(ActivityRuntime.deliver(again,whole)&&Arrays.stream(again.inventory.items).mapToInt(i->i.stackSize).sum()==81,"restart and retry cannot duplicate a whole structure");
 }
}'''}
for name,text in sources.items():p=src/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(text,encoding='utf-8')
source=Path(os.environ.get('ACTIVITY_SOURCE_ROOT',str(root)))
java=next((root.parent/'.tools/temurin25').glob('*/bin'));gson=source/'.local/engines/dc2/libraries/com/google/code/gson/gson/2.10.1/gson-2.10.1.jar';asm=source/'.local/engines/mb/libraries/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar'
cp=os.pathsep.join(map(str,[classes,root/'.local/activities/classes',gson,asm]))
for command in [[java/'javac.exe','--release','8','-cp',cp,'-d',classes,*src.rglob('*.java')],[java/'java.exe','-cp',cp,'uk.boshan.activities.MailboxCheck',out/'playerdata']]:
 p=subprocess.run(list(map(str,command)),capture_output=True,text=True,encoding='utf-8',errors='replace',creationflags=0x08000000)
 if p.returncode:raise RuntimeError(p.stdout+p.stderr)
 print(p.stdout.strip())
