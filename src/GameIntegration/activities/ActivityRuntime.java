package uk.boshan.activities;

import com.google.gson.*;
import java.io.*;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.zip.GZIPInputStream;

/** Server-only observer and durable mailbox. No HTTP or polling disk reads on a game tick. */
public final class ActivityRuntime {
    static final Gson JSON = new Gson();
    static final String LEDGER = "MojinActivitiesDelivered";
    static final ConcurrentMap<UUID,PlayerState> players = new ConcurrentHashMap<UUID,PlayerState>();
    static final ThreadLocal<Deque<Object>> context = new ThreadLocal<Deque<Object>>() { protected Deque<Object> initialValue(){return new ArrayDeque<Object>();} };
    static final ThreadLocal<Object[]> machineOutput = new ThreadLocal<Object[]>();
    static final Map<String,Long> warnings = new ConcurrentHashMap<String,Long>();
    static final Map<String,Method> methods = new ConcurrentHashMap<String,Method>();
    static final ConcurrentLinkedQueue<JsonObject> incoming = new ConcurrentLinkedQueue<JsonObject>();
    static final BlockingQueue<JsonObject> events = new ArrayBlockingQueue<JsonObject>(8192);
    static volatile Definition definition;
    static Path spool, playerData;
    static String base, secret, instance;
    static final ScheduledExecutorService worker = Executors.newSingleThreadScheduledExecutor(r -> { Thread t=new Thread(r,"Mojin-Activities-IO");t.setDaemon(true);return t; });
    static final class Definition { String id; String[] questIds, trackedItems, trackedKills; }
    static final class PlayerState {
        volatile Object player; volatile long lastPoll, aliveAt; long ticks; int questCursor;
        final Set<String> facts = new HashSet<String>();
        final Map<String,Integer> counts = new HashMap<String,Integer>();
        final Set<String> completions = new HashSet<String>();
        String day=""; final Queue<JsonObject> deliveries = new ConcurrentLinkedQueue<JsonObject>();
    }
    public static void install(Instrumentation instrument) throws Exception {
        String path=System.getProperty("mojin.activities.config","");
        if(path.isEmpty())throw new IllegalArgumentException("Missing private activity config");
        Properties props=new Properties();try(Reader reader=Files.newBufferedReader(Paths.get(path),StandardCharsets.UTF_8)){props.load(reader);}
        instance=props.getProperty("instance","");secret=props.getProperty("secret","");base=props.getProperty("baseUrl","");
        if(!instance.matches("m3e|dc2|mb|vw")||secret.length()<32)throw new IllegalArgumentException("Invalid activity configuration");
        URI uri=new URI(base);
        boolean local="http".equals(uri.getScheme()) && (("hub-api".equals(uri.getHost()) && Boolean.parseBoolean(props.getProperty("allowLocalContainerHttp","false"))) || "127.0.0.1".equals(uri.getHost()) || "localhost".equals(uri.getHost()));
        if((!"https".equals(uri.getScheme())&&!local)||uri.getUserInfo()!=null||uri.getQuery()!=null||uri.getFragment()!=null||!uri.getPath().equals("/internal/v1/activities/"+instance))throw new IllegalArgumentException("Invalid activity endpoint");
        spool=Paths.get(props.getProperty("spoolDirectory",Paths.get(path).toAbsolutePath().getParent().resolve("activity-spool").toString())).toAbsolutePath();
        playerData=Paths.get(props.getProperty("playerDataDirectory","")).toAbsolutePath();
        if(!Files.isDirectory(playerData))throw new IllegalArgumentException("Player data directory is required");
        Files.createDirectories(spool.resolve("events"));Files.createDirectories(spool.resolve("rejected"));
        instrument.addTransformer(new ActivityTransformer());
        worker.scheduleWithFixedDelay(() -> io(),0,2,TimeUnit.SECONDS);
        Runtime.getRuntime().addShutdownHook(new Thread(() -> { try{flush();}catch(Exception ignored){} },"Mojin-Activities-Flush"));
        System.out.println("[Mojin Activities] server observer ready: "+instance);
    }
    static JsonElement request(String path, JsonElement body) throws Exception {
        HttpURLConnection c=(HttpURLConnection)new URL(base+path).openConnection();
        c.setConnectTimeout(4000);c.setReadTimeout(6000);c.setInstanceFollowRedirects(false);
        c.setRequestProperty("Authorization","Bearer "+secret);c.setRequestProperty("Accept","application/json");
        try {
            if(body!=null){c.setRequestMethod("POST");c.setDoOutput(true);c.setRequestProperty("Content-Type","application/json");byte[] bytes=JSON.toJson(body).getBytes(StandardCharsets.UTF_8);c.setFixedLengthStreamingMode(bytes.length);try(OutputStream out=c.getOutputStream()){out.write(bytes);}}
            int status=c.getResponseCode();
            if(status!=200)throw new HttpFailure(status);
            try(InputStream input=c.getInputStream();ByteArrayOutputStream bytes=new ByteArrayOutputStream()) {byte[] buffer=new byte[4096];int size;while((size=input.read(buffer))!=-1){bytes.write(buffer,0,size);if(bytes.size()>1024*1024)throw new IOException("Oversized activity response");}return new JsonParser().parse(new String(bytes.toByteArray(),StandardCharsets.UTF_8));}
        } finally { c.disconnect(); }
    }
    static final class HttpFailure extends IOException { final int status; HttpFailure(int code){super("HTTP "+code);status=code;} }
    static void io() {
        try {
            flush();
            if(definition==null){Definition next=JSON.fromJson(request("/definition",null),Definition.class);if(!instance.equals(next.id)||next.questIds==null||next.trackedItems==null)throw new IOException("Activity definition mismatch");definition=next;System.out.println("[Mojin Activities] rules loaded: "+next.questIds.length+" quests");}
            try(DirectoryStream<Path> paths=Files.newDirectoryStream(spool.resolve("events"),"*.json")) {
                int count=0;
                for(Path file:paths){if(++count>48)break;JsonObject e=new JsonParser().parse(new String(Files.readAllBytes(file),StandardCharsets.UTF_8)).getAsJsonObject();try{
                    String ack=e.has("deliveryAck")?e.get("deliveryAck").getAsString():null;
                    if(ack!=null)request("/deliveries/"+e.get("gameUuid").getAsString()+"/"+ack+"/ack",new JsonObject());else request("/events",e);
                    Files.delete(file);
                }catch(HttpFailure failure){if(failure.status==400||failure.status==403||failure.status==409){Files.move(file,spool.resolve("rejected").resolve(file.getFileName()),StandardCopyOption.REPLACE_EXISTING);problem("rejected-event",failure);}else if(failure.status==404){problem("identity-not-ready",failure);}else throw failure;}}
            }
            long now=System.currentTimeMillis();
            for(Map.Entry<UUID,PlayerState> entry:players.entrySet()){
                PlayerState s=entry.getValue();if(now-s.aliveAt>15000||now-s.lastPoll<10000||!s.deliveries.isEmpty())continue;
                s.lastPoll=now;
                JsonArray array=request("/deliveries/"+entry.getKey(),null).getAsJsonArray();for(JsonElement e:array)s.deliveries.offer(e.getAsJsonObject());
            }
        } catch(Throwable error){problem("connection",error);}
    }
    static synchronized void flush() throws Exception {
        JsonObject event;while((event=events.peek())!=null){
            Path file=spool.resolve("events").resolve(event.get("eventId").getAsString()+".json");Path temp=file.resolveSibling(file.getFileName()+".tmp");
            try(FileOutputStream out=new FileOutputStream(temp.toFile())){out.write(JSON.toJson(event).getBytes(StandardCharsets.UTF_8));out.getFD().sync();}
            Files.move(temp,file,StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE);events.poll();
        }
    }
    static boolean submit(UUID uuid,String kind,String key,int count,Collection<String> facts,UUID stable) {
        JsonObject e=new JsonObject();e.addProperty("eventId",(stable==null?UUID.randomUUID():stable).toString());e.addProperty("gameUuid",uuid.toString());e.addProperty("occurredAt",Instant.now().toString());e.addProperty("kind",kind);e.addProperty("key",key);e.addProperty("count",Math.min(4096,Math.max(1,count)));if(facts!=null)e.add("facts",JSON.toJsonTree(facts));
        if(events.offer(e))return true;problem("outbox-full",new IOException("Activity event queue full"));return false;
    }
    static UUID uuid(Object player) throws Exception {return (UUID)call(player,new String[]{"getUniqueID","func_110124_au","getUUID","m_20148_"});}
    static boolean serverPlayer(Object player) {if(player==null)return false;for(Class<?> c=player.getClass();c!=null;c=c.getSuperclass())if(c.getName().equals("net.minecraft.entity.player.EntityPlayerMP")||c.getName().equals("net.minecraft.server.level.ServerPlayer"))return true;return false;}
    static PlayerState state(Object player) throws Exception {UUID id=uuid(player);PlayerState s=players.get(id);if(s==null){s=new PlayerState();s.player=player;players.put(id,s);}s.player=player;s.aliveAt=System.currentTimeMillis();return s;}
    public static void tick(Object player) {
        if(definition==null||!serverPlayer(player))return;
        try {
            PlayerState s=state(player);if(++s.ticks%20!=0)return;
            snapshot(player,s,32);
            JsonObject next=s.deliveries.peek();
            if(next!=null&&deliver(player,next)){s.deliveries.poll();JsonObject ack=new JsonObject();ack.addProperty("eventId",UUID.randomUUID().toString());ack.addProperty("gameUuid",uuid(player).toString());ack.addProperty("deliveryAck",next.get("id").getAsString());events.offer(ack);}
        }catch(Throwable error){problem("player-tick",error);}
    }
    public static void logout(Object player){try{if(serverPlayer(player))players.remove(uuid(player));}catch(Exception ignored){}}
    public static void questContext(Object player){context.get().push(player==null?ActivityRuntime.class:player);}
    public static void clearQuestContext(){Deque<Object> stack=context.get();if(!stack.isEmpty())stack.pop();if(stack.isEmpty())context.remove();}
    public static void bqComplete(Object quest,UUID player,long timestamp) {
        if(definition==null)return;
        try {
            Object active=context.get().peek();if(!serverPlayer(active)||!uuid(active).equals(player))return;
            Object db=type(active,"betterquesting.questing.QuestDatabase").getField("INSTANCE").get(null);
            String id=String.valueOf(call(db,new String[]{"lookupKey","getID"},quest));
            if(!contains(definition.questIds,id))return;
            PlayerState s=state(active);String marker=id+":"+timestamp;if(s.completions.contains(marker))return;
            if(submit(player,"quest",id,1,null,UUID.nameUUIDFromBytes((instance+":"+player+":"+marker).getBytes(StandardCharsets.UTF_8))))s.completions.add(marker);
        }catch(Throwable error){problem("bq-completion",error);}
    }
    public static void ftbComplete(boolean changed,Object team,long id,Date date) {
        if(!changed||definition==null)return;
        try {
            String key=String.format("%016X",id);if(!contains(definition.questIds,key))return;
            Object file=call(team,new String[]{"getFile"});Object player=call(file,new String[]{"getCurrentPlayer"});if(!serverPlayer(player))return;
            UUID uuid=uuid(player);submit(uuid,"quest",key,1,null,UUID.nameUUIDFromBytes((instance+":"+uuid+":"+key+":"+date.getTime()).getBytes(StandardCharsets.UTF_8)));
        }catch(Throwable error){problem("ftb-completion",error);}
    }
    static void snapshot(Object player,PlayerState s,int budget) throws Exception {
        UUID uuid=uuid(player);List<String> fresh=new ArrayList<String>();Object db,team=null;
        boolean ftb=instance.equals("dc2");
        if(ftb){db=type(player,"dev.ftb.mods.ftbquests.quest.ServerQuestFile").getField("INSTANCE").get(null);if(db==null)return;team=call(type(player,"dev.ftb.mods.ftbquests.quest.TeamData"),new String[]{"get"},player);if(team==null)return;}
        else db=type(player,"betterquesting.questing.QuestDatabase").getField("INSTANCE").get(null);
        for(int i=0;i<budget;i++){
            String id=definition.questIds[s.questCursor++ % definition.questIds.length];Object quest;
            if(ftb)quest=call(db,new String[]{"getQuest"},Long.parseUnsignedLong(id,16));
            else quest=instance.equals("mb")?call(db,new String[]{"getValue"},Integer.parseInt(id)):call(db,new String[]{"get"},UUID.fromString(id));
            if(quest==null)continue;
            boolean complete=(Boolean)(ftb?call(team,new String[]{"isCompleted"},quest):call(quest,new String[]{"isComplete"},uuid));
            boolean unlocked=(Boolean)(ftb?call(team,new String[]{"areDependenciesComplete"},quest):call(quest,new String[]{"isUnlocked"},uuid));
            if(complete&&s.facts.add("quest:"+id))fresh.add("quest:"+id);
            if(unlocked&&s.facts.add("unlocked:"+id))fresh.add("unlocked:"+id);
        }
        if(!fresh.isEmpty()&&!submit(uuid,"snapshot","",1,fresh,null))s.facts.removeAll(fresh);
    }
    public static void crafted(Object player,Object stack) {
        if(definition==null||!serverPlayer(player)||stack==null)return;
        try {
            Object nbt=tag(player);call(stack,new String[]{"writeToNBT","func_77955_b","save","m_41739_"},nbt);
            String id;
            if(instance.equals("dc2"))id=string(nbt,"id");
            else {Object item=call(stack,new String[]{"getItem","func_77973_b"});Object registry=field(type(player,"net.minecraft.item.Item"),"itemRegistry","field_150901_e","REGISTRY","field_150901_e");id=String.valueOf(call(registry,new String[]{"getNameForObject","func_148750_c","getNameForObject","func_177774_c"},item));}
            int meta=instance.equals("dc2")?0:((Number)call(nbt,new String[]{"getShort","func_74765_d"},"Damage")).intValue();
            String key=id+"@"+meta;if(!contains(definition.trackedItems,key))return;
            // Reviewed rewards all have empty item NBT. Tagged/enhanced items cannot unlock an untagged proof.
            int count=((Number)call(nbt,new String[]{"getByte","func_74771_c","m_128445_"},"Count")).intValue();
            if(count<=0)return;record(player,"craft",key,count);
        }catch(Throwable error){problem("craft",error);}
    }
    // GT6's doActive calls addStackToSlot only for real completed recipe outputs. Transfers,
    // repeated item pickup and an idle machine never reach this observer.
    public static void beginMachineOutput(Object stack,Object machine) {
        machineOutput.remove();if(definition==null||!instance.equals("vw")||stack==null)return;
        try {
            Object owner=field(machine,"mOwner");if(!(owner instanceof UUID))return;
            PlayerState state=players.get(owner);if(state==null||System.currentTimeMillis()-state.aliveAt>15000)return;
            machineOutput.set(new Object[]{state.player,call(stack,new String[]{"copy","func_77946_l"})});
        }catch(Throwable e){problem("gt6-production",e);}
    }
    public static void finishMachineOutput(boolean inserted) {
        Object[] result=machineOutput.get();machineOutput.remove();if(inserted&&result!=null)crafted(result[0],result[1]);
    }
    public static void killed(boolean cancelled,Object entity,Object source) {
        if(cancelled||definition==null)return;
        try {
            Object player=call(source,new String[]{"getEntity","func_76346_g","getTrueSource","getEntity","m_7639_"});if(!serverPlayer(player))return;
            String id;
            if(instance.equals("dc2")){
                Object type=call(entity,new String[]{"getType","m_6095_"});Object registry=field(ActivityRuntime.type(player,"net.minecraft.core.registries.BuiltInRegistries"),"ENTITY_TYPE","f_256780_");id=String.valueOf(call(registry,new String[]{"getKey","m_7981_"},type));
            }else id=String.valueOf(call(type(player,"net.minecraft.entity.EntityList"),new String[]{"getEntityString","func_75621_b","getKey","func_191301_a"},entity));
            if(!id.contains(":")&&!id.contains("."))id="minecraft:"+id.toLowerCase(Locale.ROOT);
            if(contains(definition.trackedKills,id))record(player,"kill",id,1);
        }catch(Throwable error){problem("kill",error);}
    }
    static void record(Object player,String kind,String key,int count) throws Exception {
        PlayerState s=state(player);String day=LocalDate.now(ZoneOffset.ofHours(8)).toString();
        if(!day.equals(s.day)){s.day=day;s.counts.clear();}
        String k=kind+":"+key;int previous=s.counts.containsKey(k)?s.counts.get(k):0;if(previous>=32)return;
        int delta=Math.min(count,32-previous);if(submit(uuid(player),kind,key,delta,null,null)){s.counts.put(k,previous+delta);s.facts.add(k);}
    }
    static Object persistent(Object player) throws Exception {return call(player,new String[]{"getEntityData","getPersistentData"});}
    public static void clonePlayer(Object player,Object original) {
        if(!serverPlayer(player)||!serverPlayer(original))return;
        try{String ledger=string(persistent(original),LEDGER);setString(persistent(player),LEDGER,ledger);}catch(Throwable error){problem("clone-ledger",error);}
    }
    static boolean deliver(Object player,JsonObject delivery) throws Exception {
        String id=delivery.get("id").getAsString();UUID.fromString(id);
        Object data=persistent(player);String previous=string(data,LEDGER);
        if(previous.contains(id)){if(!persisted(player,id))savePlayer(player);return persisted(player,id);}
        Object inventory=instance.equals("dc2")?call(player,new String[]{"getInventory","m_150109_"}):field(player,"inventory","field_71071_by");
        int size=((Number)call(inventory,new String[]{"getSizeInventory","func_70302_i_","getContainerSize","m_6643_"})).intValue();
        List<Object> backup=new ArrayList<Object>();
        for(int slot=0;slot<size;slot++){Object stack=call(inventory,new String[]{"getStackInSlot","func_70301_a","getItem","m_8020_"},slot);backup.add(stack==null?null:call(stack,new String[]{"copy","func_77946_l","m_41777_"}));}
        boolean done=false;
        try {
            JsonArray items=delivery.getAsJsonArray("items");if(items.size()>16)throw new IOException("Invalid delivery");
            for(JsonElement raw:items){JsonObject i=raw.getAsJsonObject();Object stack=makeStack(player,i);
                if(stack==null||!(Boolean)call(inventory,new String[]{"addItemStackToInventory","func_70441_a","add","m_36054_"},stack))return false;
                int remaining=instance.equals("dc2")||instance.equals("mb")?((Number)call(stack,new String[]{"getCount","func_190916_E","m_41613_"})).intValue():((Number)field(stack,"stackSize","field_77994_a")).intValue();
                if(remaining>0)return false;
            }
            setString(data,LEDGER,previous+"\n"+id);
            // Keep the inventory and ledger together if disk saving fails. The next tick retries
            // saving this same delivery; it never adds the stacks twice or rolls back a saved reward.
            done=true;
            savePlayer(player);
            return persisted(player,id);
        }finally{
            if(!done){for(int slot=0;slot<size;slot++)call(inventory,new String[]{"setInventorySlotContents","func_70299_a","setItem","m_6836_"},slot,backup.get(slot));setString(data,LEDGER,previous);}
        }
    }
    static Object makeStack(Object player,JsonObject item) throws Exception {
        String id=item.get("id").getAsString();int count=item.get("count").getAsInt();int meta=item.get("meta").getAsInt();
        if(count<1||count>64||!item.get("nbt").getAsString().equals("{}"))throw new IOException("Unreviewed item NBT");
        Object nbt=tag(player);setString(nbt,"id",id);call(nbt,new String[]{"setByte","func_74774_a","putByte","m_128344_"},"Count",(byte)count);
        if(!instance.equals("dc2"))call(nbt,new String[]{"setShort","func_74777_a"},"Damage",(short)meta);
        Class<?> c=type(player,instance.equals("dc2")?"net.minecraft.world.item.ItemStack":"net.minecraft.item.ItemStack");
        if(instance.equals("dc2"))return call(c,new String[]{"of","m_41712_"},nbt);
        Class<?> itemClass=type(player,"net.minecraft.item.Item");Object registry=field(itemClass,"itemRegistry","field_150901_e","REGISTRY");
        Object key=instance.equals("mb")?type(player,"net.minecraft.util.ResourceLocation").getConstructor(String.class).newInstance(id):id;
        Object itemType=call(registry,new String[]{"getObject","func_82594_a"},key);
        if(itemType==null)throw new IOException("Unknown reviewed item");
        return c.getConstructor(itemClass,int.class,int.class).newInstance(itemType,count,meta);
    }
    static void savePlayer(Object player) throws Exception {
        Object server;
        if(instance.equals("dc2"))server=call(player,new String[]{"getServer","m_20194_"});
        else {Class<?> f=type(player,instance.equals("mb")?"net.minecraftforge.fml.common.FMLCommonHandler":"cpw.mods.fml.common.FMLCommonHandler");server=call(call(f,new String[]{"instance"}),new String[]{"getMinecraftServerInstance"});}
        Object list=call(server,new String[]{"getConfigurationManager","func_71203_ab","getPlayerList","func_184103_al","m_6846_"});
        call(list,new String[]{"writePlayerData","func_72391_b","save","m_6765_"},player);
    }
    static boolean persisted(Object player,String id) throws Exception {
        Path file=playerData.resolve(uuid(player)+".dat");if(!Files.isRegularFile(file))return false;
        Class<?> io=type(player,instance.equals("dc2")?"net.minecraft.nbt.NbtIo":"net.minecraft.nbt.CompressedStreamTools");
        Object root;try(InputStream input=Files.newInputStream(file)){root=call(io,new String[]{"readCompressed","func_74796_a","m_128939_"},input);}
        Object forge=call(root,new String[]{"getCompoundTag","func_74775_l","getCompound","m_128469_"},"ForgeData");
        return string(forge,LEDGER).contains(id);
    }
    static Object tag(Object player) throws Exception{return type(player,instance.equals("dc2")?"net.minecraft.nbt.CompoundTag":"net.minecraft.nbt.NBTTagCompound").getConstructor().newInstance();}
    static String string(Object tag,String key) throws Exception{return String.valueOf(call(tag,new String[]{"getString","func_74779_i","m_128461_"},key));}
    static void setString(Object tag,String key,String value) throws Exception{call(tag,new String[]{"setString","func_74778_a","putString","m_128359_"},key,value);}
    static Class<?> type(Object context,String name) throws Exception{return Class.forName(name,false,context.getClass().getClassLoader());}
    static boolean contains(String[] items,String item){for(String s:items)if(s.equals(item))return true;return false;}
    static Object field(Object target,String...names) throws Exception{for(Class<?> c=target instanceof Class?(Class<?>)target:target.getClass();c!=null;c=c.getSuperclass())for(String n:names)try{Field f=c.getDeclaredField(n);f.setAccessible(true);return f.get(target instanceof Class?null:target);}catch(NoSuchFieldException ignored){}throw new NoSuchFieldException(Arrays.toString(names));}
    static Object call(Object target,String[] names,Object...args) throws Exception {
        Class<?> cls=target instanceof Class?(Class<?>)target:target.getClass();StringBuilder key=new StringBuilder(cls.getName()).append(Arrays.toString(names));for(Object a:args)key.append(':').append(a==null?"null":a.getClass().getName());
        Method cached=methods.get(key.toString());if(cached!=null)return cached.invoke(target instanceof Class?null:target,args);
        for(Class<?> c=cls;c!=null;c=c.getSuperclass())for(Method m:c.getDeclaredMethods()){
            if(!contains(names,m.getName())||m.getParameterTypes().length!=args.length)continue;
            Class<?>[] ps=m.getParameterTypes();boolean match=true;
            for(int i=0;i<args.length;i++){Class<?> p=ps[i];if(p.isPrimitive())p=p==int.class?Integer.class:p==long.class?Long.class:p==byte.class?Byte.class:p==short.class?Short.class:p==boolean.class?Boolean.class:p;if(args[i]!=null&&!p.isInstance(args[i])){match=false;break;}}
            if(!match)continue;m.setAccessible(true);methods.put(key.toString(),m);return m.invoke(target instanceof Class?null:target,args);
        }
        throw new NoSuchMethodException(cls.getName()+Arrays.toString(names));
    }
    public static void problem(String stage,Throwable error){long now=System.currentTimeMillis();Long previous=warnings.put(stage,now);if(previous==null||now-previous>60000){Throwable e=error instanceof InvocationTargetException&&error.getCause()!=null?error.getCause():error;System.err.println("[Mojin Activities] "+stage+": "+e.getClass().getSimpleName()+" (progress retained; see private diagnostics)");if(spool!=null)try{Files.write(spool.resolve("last-error.txt"),(stage+"\n"+e.toString()+"\n").getBytes(StandardCharsets.UTF_8));}catch(Exception ignored){}}}
}
