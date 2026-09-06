import java.io.*;
import java.lang.reflect.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;
import com.sun.net.httpserver.HttpServer;
import io.netty.channel.*;
import io.netty.channel.embedded.EmbeddedChannel;
import org.objectweb.asm.*;
import org.objectweb.asm.commons.*;
import org.objectweb.asm.util.CheckClassAdapter;
import uk.boshan.join.JoinRuntime;

public class WireDecodeCheck {
    static void set(String key,Object value)throws Exception {Field f=JoinRuntime.class.getDeclaredField(key);f.setAccessible(true);f.set(null,value);}
    static void check(boolean ok,String message){if(!ok)throw new AssertionError(message);}
    static class Loader extends ClassLoader {
        final String name; final byte[] bytes;
        Loader(String name,byte[] bytes){super(WireDecodeCheck.class.getClassLoader());this.name=name;this.bytes=bytes;}
        protected Class<?> loadClass(String name,boolean resolve)throws ClassNotFoundException {
            if(!this.name.equals(name))return super.loadClass(name,resolve);
            Class<?> type=findLoadedClass(name);if(type==null)type=defineClass(name,bytes,0,bytes.length);if(resolve)resolveClass(type);return type;
        }
    }
    public static class Manager {
        int delivered;
        public void channelRead0(ChannelHandlerContext context,net.minecraft.network.Packet packet){if(!JoinRuntime.read(this,context,packet))delivered++;}
        public void channelRead0(ChannelHandlerContext context,net.minecraft.network.protocol.Packet packet){if(!JoinRuntime.read(this,context,packet))delivered++;}
    }
    static void varint(ByteArrayOutputStream out,int value){while((value&~127)!=0){out.write((value&127)|128);value>>>=7;}out.write(value);}
    static byte[] wire(String host,int protocol)throws Exception {ByteArrayOutputStream out=new ByteArrayOutputStream();varint(out,protocol);byte[] text=host.getBytes(StandardCharsets.UTF_8);varint(out,text.length);out.write(text);out.write(25565>>>8);out.write(25565&255);varint(out,2);return out.toByteArray();}
    static byte[] remapLegacy(byte[] original){
        Map<String,String> names=new HashMap<>();names.put("md","net/minecraft/network/handshake/client/C00Handshake");names.put("gy","net/minecraft/network/PacketBuffer");names.put("gx","net/minecraft/network/EnumConnectionState");names.put("ht","net/minecraft/network/Packet");names.put("me","net/minecraft/network/handshake/INetHandlerHandshakeServer");names.put("hb","net/minecraft/network/INetHandler");
        ClassWriter output=new ClassWriter(0);new ClassReader(original).accept(new ClassRemapper(output,new SimpleRemapper(names)),0);return output.toByteArray();
    }
    public static void main(String[] args)throws Exception {
        set("server",true);set("mode","enforce");set("instance","mb");set("secret","test-secret-0123456789abcdef");
        Path config=Files.createTempFile("join-wire-", ".properties");Files.write(config,"mode=enforce\n".getBytes(StandardCharsets.UTF_8));config.toFile().deleteOnExit();set("configPath",config.toString());
        AtomicInteger consumed=new AtomicInteger();HttpServer api=HttpServer.create(new InetSocketAddress("127.0.0.1",0),0);
        api.createContext("/redeem",exchange->{try {String body=new String(exchange.getRequestBody().readAllBytes(),StandardCharsets.UTF_8);check(body.contains("JoinAudit"),"exact name to API");consumed.incrementAndGet();String uuid=UUID.nameUUIDFromBytes("OfflinePlayer:JoinAudit".getBytes(StandardCharsets.UTF_8)).toString();byte[] response=("{\"allowed\":true,\"gameName\":\"JoinAudit\",\"gameUuid\":\""+uuid+"\"}").getBytes(StandardCharsets.UTF_8);exchange.sendResponseHeaders(200,response.length);exchange.getResponseBody().write(response);}finally{exchange.close();}});api.start();
        set("endpoint","http://127.0.0.1:"+api.getAddress().getPort()+"/redeem");
        try {
            for(int which=0;which<args.length;which++){
                boolean legacy=which==0;String name=legacy?"net.minecraft.network.handshake.client.C00Handshake":"net.minecraft.network.protocol.handshake.ClientIntentionPacket";
                byte[] source=Files.readAllBytes(Paths.get(args[which]));if(legacy)source=remapLegacy(source);
                byte[] transformed=JoinRuntime.transform(name.replace('.','/'),source);
                new ClassReader(transformed).accept(new CheckClassAdapter(new ClassWriter(0),true),0);
                Class<?> type=new Loader(name,transformed).loadClass(name);
                for(boolean authenticated:new boolean[]{true,false}){
                    String token=String.join("",Collections.nCopies(43,which==0?"A":"B"));String suffix=legacy?"\0FML\0":"\0FML3\0";
                    String host="play.example"+(authenticated?"\0MOJIN1\0"+token:"")+suffix;
                    Object packet;
                    if(legacy){packet=type.getConstructor().newInstance();type.getMethod("a",net.minecraft.network.PacketBuffer.class).invoke(packet,new net.minecraft.network.PacketBuffer(wire(host,340)));check((Boolean)type.getMethod("hasFMLMarker").invoke(packet),"Forge 1.12 FML marker preserved");}
                    else{packet=type.getConstructor(net.minecraft.network.FriendlyByteBuf.class).newInstance(new net.minecraft.network.FriendlyByteBuf(wire(host,763)));try{check(type.getMethod("getFMLVersion").invoke(packet).equals("FML3"),"Forge 1.20 FML3 preserved");}catch(NoSuchMethodException vanilla){}}
                    Manager manager=new Manager();EmbeddedChannel channel=new EmbeddedChannel(new ChannelInboundHandlerAdapter());ChannelHandlerContext context=channel.pipeline().firstContext();
                    JoinRuntime.read(manager,context,packet);int before=consumed.get();
                    Object login=legacy?new net.minecraft.network.login.client.CPacketLoginStart("JoinAudit"):new net.minecraft.network.protocol.login.ServerboundHelloPacket("JoinAudit");
                    check(JoinRuntime.read(manager,context,login),"login held before redemption");
                    for(int n=0;n<200&&channel.isActive()&&manager.delivered==0;n++){channel.runPendingTasks();Thread.sleep(10);}
                    if(authenticated)check(consumed.get()==before+1&&manager.delivered==1&&channel.isActive(),"wire -> real decoder -> successful gate");
                    else check(consumed.get()==before&&manager.delivered==0&&!channel.isActive(),"no ticket rejected before login");
                    channel.close();
                }
                System.out.println("WIRE_DECODE_GATE_PASS "+Paths.get(args[which]).getFileName());
            }
        }finally{api.stop(0);}
    }
}
