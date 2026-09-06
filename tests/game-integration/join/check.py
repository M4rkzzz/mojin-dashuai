"""Bounded join-gate tests: actual Java/Windows IPC and Netty async pre-login gate.

Protocol fixture classes preserve the fixed runtime class names and method shapes.
They are not a replacement for the four real-server positive/negative rollout checks.
"""
from pathlib import Path
import ctypes, hashlib, http.server, json, os, socket, subprocess, threading, uuid
from ctypes import wintypes

root = Path(__file__).resolve().parents[3]
work = root / '.local/join-agent/check'
work.mkdir(parents=True, exist_ok=True)
java_bin = next((root.parent / '.tools/temurin25').glob('*/bin'))
jar = root / 'src/GameIntegration/join/mojin-join-agent.jar'
netty = root / '.local/loading-live-20260906/instances/vw/libraries/io/netty/netty-all/4.0.10.Final/netty-all-4.0.10.Final.jar'
source = {
 'net/minecraft/network/Packet.java': 'package net.minecraft.network; public interface Packet {}',
 'net/minecraft/network/PlayPacket.java': 'package net.minecraft.network; public class PlayPacket implements Packet {}',
 'net/minecraft/network/Protocol.java': 'package net.minecraft.network; public enum Protocol { STATUS, LOGIN }',
 'net/minecraft/network/NetworkManager.java': '''package net.minecraft.network; public class NetworkManager { public int seen; public void injectedMixinCallback(io.netty.channel.ChannelHandlerContext ctx, Packet packet, String callback) { throw new AssertionError("must not hook a mixin callback"); } public void exceptionCaught(io.netty.channel.ChannelHandlerContext ctx, Throwable error) { throw new AssertionError("must not dispatch here"); } public void channelRead0(io.netty.channel.ChannelHandlerContext ctx, Packet packet) { seen++; } }''',
 'net/minecraft/network/Connection.java': '''package net.minecraft.network; public class Connection { public int seen; public void exceptionCaught(io.netty.channel.ChannelHandlerContext ctx, Throwable error) { throw new AssertionError("must not dispatch here"); } public void channelRead0(io.netty.channel.ChannelHandlerContext ctx, net.minecraft.network.protocol.Packet packet) { seen++; } }''',
 'net/minecraft/network/handshake/client/C00Handshake.java': '''package net.minecraft.network.handshake.client; public class C00Handshake implements net.minecraft.network.Packet { public String host; public net.minecraft.network.Protocol intent; public C00Handshake(String host, net.minecraft.network.Protocol intent) { this.host=host; this.intent=intent; } }''',
 'net/minecraft/network/login/client/C00PacketLoginStart.java': '''package net.minecraft.network.login.client; public class C00PacketLoginStart implements net.minecraft.network.Packet { public String name; public C00PacketLoginStart(String name) { this.name=name; } }''',
 'net/minecraft/network/login/client/CPacketLoginStart.java': '''package net.minecraft.network.login.client; public class CPacketLoginStart implements net.minecraft.network.Packet { public String name; public CPacketLoginStart(String name) { this.name=name; } }''',
 'net/minecraft/network/protocol/Packet.java': 'package net.minecraft.network.protocol; public interface Packet {}',
 'net/minecraft/network/protocol/handshake/ClientIntentionPacket.java': '''package net.minecraft.network.protocol.handshake; public class ClientIntentionPacket implements net.minecraft.network.protocol.Packet { public final String host; public net.minecraft.network.Protocol intent; public ClientIntentionPacket(String host, net.minecraft.network.Protocol intent) { this.host=host; this.intent=intent; } }''',
 'net/minecraft/network/protocol/login/ServerboundHelloPacket.java': '''package net.minecraft.network.protocol.login; public class ServerboundHelloPacket implements net.minecraft.network.protocol.Packet { private final String name; public ServerboundHelloPacket(String name) { this.name=name; } }''',
 'JoinPipeCheck.java': '''import uk.boshan.join.JoinRuntime; public class JoinPipeCheck { public static void main(String[] args) throws Exception { Class<?> type=Class.forName(args.length==0?"net.minecraft.network.handshake.client.C00Handshake":"net.minecraft.network.protocol.handshake.ClientIntentionPacket"); java.lang.reflect.Constructor<?> constructor=type.getConstructor(String.class,net.minecraft.network.Protocol.class); Object status=constructor.newInstance("status.example",net.minecraft.network.Protocol.STATUS); if(!type.getField("host").get(status).equals("status.example")) throw new AssertionError("status touched"); Object login=constructor.newInstance("play.example",net.minecraft.network.Protocol.LOGIN); String host=(String)type.getField("host").get(login); if (!host.equals("play.example\\0MOJIN1\\0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")) throw new AssertionError("wrong authenticated handshake"); System.out.println("PIPE_PASS"); } }''',
 'JoinGateCheck.java': r'''
import uk.boshan.join.JoinRuntime;
import net.minecraft.network.*;
import io.netty.channel.*;
import io.netty.channel.embedded.EmbeddedChannel;
import java.lang.reflect.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.sun.net.httpserver.*;
public class JoinGateCheck {
 static int redeemed;
 static final String ticket="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
 static final Set<String> consumed=new HashSet<String>();
 static void check(boolean value,String text){ if(!value) throw new AssertionError(text); }
 static void tick(EmbeddedChannel c,int wanted,Object manager) throws Exception { for(int n=0;n<300;n++){ c.runPendingTasks(); if(manager.getClass().getField("seen").getInt(manager)>=wanted || !c.isActive()) return; Thread.sleep(10); } }
 static Object handshake(boolean modern,String host,Protocol protocol)throws Exception { return Class.forName(modern?"net.minecraft.network.protocol.handshake.ClientIntentionPacket":"net.minecraft.network.handshake.client.C00Handshake").getConstructor(String.class,Protocol.class).newInstance(host,protocol); }
 static Object login(boolean modern,boolean cleanroom,String name)throws Exception { return Class.forName(modern?"net.minecraft.network.protocol.login.ServerboundHelloPacket":cleanroom?"net.minecraft.network.login.client.CPacketLoginStart":"net.minecraft.network.login.client.C00PacketLoginStart").getConstructor(String.class).newInstance(name); }
 static void run(boolean modern,boolean cleanroom)throws Exception {
  Class<?> type=Class.forName(modern?"net.minecraft.network.Connection":"net.minecraft.network.NetworkManager");
  Method read=type.getDeclaredMethod("channelRead0",ChannelHandlerContext.class,Class.forName(modern?"net.minecraft.network.protocol.Packet":"net.minecraft.network.Packet"));
  for(int scenario=0;scenario<9;scenario++) {
   Object manager=type.newInstance(); EmbeddedChannel c=new EmbeddedChannel(new ChannelInboundHandlerAdapter()); ChannelHandlerContext ctx=c.pipeline().firstContext();
   String token=ticket.substring(0,42)+"abcdefghijklmnopqrstuvwxyz0123456789".charAt((scenario==6?1:scenario)+(modern?9:cleanroom?18:0));
   String host="play.example\0FML\0";
   if(scenario!=0) host="play.example\0MOJIN1\0"+token+"\0FML\0";
   Object handshake=handshake(modern,host,scenario==5?Protocol.STATUS:Protocol.LOGIN);
   read.invoke(manager,ctx,handshake);
   if(scenario==5) { check(c.isActive(),"status must pass"); c.close(); continue; }
   check(handshake.getClass().getField("host").get(handshake).equals("play.example\0FML\0"),"FML bytes preserved");
   int before=type.getField("seen").getInt(manager);
   Object login=login(modern,cleanroom,scenario==2?"WrongName":scenario==7?"WrongUuid":"JoinAudit"); read.invoke(manager,ctx,login);
   check(type.getField("seen").getInt(manager)==before,"login never delivered before async redemption");
   if(scenario==3) read.invoke(manager,ctx,login);
   tick(c,before+1,manager);
   if(scenario==1 || scenario==4) { check(c.isActive(),"valid stays connected"); check(type.getField("seen").getInt(manager)==before+1,"valid exactly one delivery"); }
   else check(!c.isActive(),"invalid connection closed before login");
   if(scenario==4) { read.invoke(manager,ctx,login); check(!c.isActive(),"duplicate login refused"); }
   c.close();
  }
 }
 static long stamp=System.currentTimeMillis()+1000;
 static void setMode(String mode)throws Exception {
  java.nio.file.Path p=java.nio.file.Paths.get(System.getProperty("mojin.join.server.config"));
  String text=new String(java.nio.file.Files.readAllBytes(p),StandardCharsets.UTF_8).replaceFirst("mode=[^\\r\\n]*","mode="+mode);
  java.nio.file.Files.write(p,text.getBytes(StandardCharsets.UTF_8)); p.toFile().setLastModified(stamp+=1000);
 }
 static void hotModes()throws Exception {
  Object old=null; EmbeddedChannel oldChannel=null; ChannelHandlerContext oldContext=null;
  for(String mode:new String[]{"observe","off","enforce","invalid"}) {
   setMode(mode); NetworkManager manager=new NetworkManager(); EmbeddedChannel c=new EmbeddedChannel(new ChannelInboundHandlerAdapter()); ChannelHandlerContext ctx=c.pipeline().firstContext();
   manager.channelRead0(ctx,(Packet)handshake(false,"play.example\0FML\0",Protocol.LOGIN));
   manager.channelRead0(ctx,(Packet)login(false,false,"JoinAudit"));
   if(mode.equals("observe") || mode.equals("off")) check(c.isActive() && manager.seen==2,"hot allow "+mode);
   else check(!c.isActive() && manager.seen==1,"hot enforce "+mode);
   if(mode.equals("observe")) { old=manager; oldChannel=c; oldContext=ctx; } else c.close();
  }
  ((NetworkManager)old).channelRead0(oldContext,new PlayPacket());
  check(oldChannel.isActive() && ((NetworkManager)old).seen==3,"already admitted connection survives enforce"); oldChannel.close(); setMode("enforce");
 }
 public static void main(String[] args)throws Exception { run(false,false); run(false,true); run(true,false); hotModes(); System.out.println("GATE_PASS"); }
}
'''
}
files=[]
for name,text in source.items():
    path=work/'src'/name; path.parent.mkdir(parents=True,exist_ok=True); path.write_text(text,encoding='utf-8'); files.append(str(path))
classes=work/'classes'; classes.mkdir(exist_ok=True)
cp=os.pathsep.join(map(str,[jar,netty]))
def run(args,timeout=45):
    result=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=timeout,creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode: raise RuntimeError(result.stdout+result.stderr)
    return result.stdout+result.stderr
run([java_bin/'javac.exe','-J-Duser.language=en','--release','8','-Xlint:-options','-encoding','UTF-8','-cp',cp,'-d',classes,*files])
classpath=os.pathsep.join(map(str,[classes,jar,netty]))
prepared=json.loads((root/'.local/loading-live-20260906/prepared.json').read_text(encoding='utf-8'))
javas=list(dict.fromkeys([prepared['vw']['java'],prepared['dc2']['java'],prepared['mb']['java']]))
results=[]

k=ctypes.WinDLL('kernel32',use_last_error=True)
k.CreateNamedPipeW.restype=wintypes.HANDLE
k.CreateNamedPipeW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,wintypes.DWORD,wintypes.DWORD,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p]
k.ConnectNamedPipe.argtypes=[wintypes.HANDLE,ctypes.c_void_p]
k.ReadFile.argtypes=[wintypes.HANDLE,ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD),ctypes.c_void_p]
k.WriteFile.argtypes=k.ReadFile.argtypes
k.FlushFileBuffers.argtypes=[wintypes.HANDLE]
k.DisconnectNamedPipe.argtypes=[wintypes.HANDLE]
k.CloseHandle.argtypes=[wintypes.HANDLE]
for java in javas:
    pipe='mojin-join-'+uuid.uuid4().hex
    handle=k.CreateNamedPipeW(rf'\\.\pipe\{pipe}',3,0,1,4096,4096,0,None)
    if handle==wintypes.HANDLE(-1).value: raise ctypes.WinError(ctypes.get_last_error())
    errors=[]
    def serve():
        try:
            if not k.ConnectNamedPipe(handle,None) and ctypes.get_last_error()!=535: raise ctypes.WinError(ctypes.get_last_error())
            buffer=ctypes.create_string_buffer(4096); count=wintypes.DWORD()
            if not k.ReadFile(handle,buffer,4096,ctypes.byref(count),None): raise ctypes.WinError(ctypes.get_last_error())
            request=json.loads(buffer.raw[:count.value]); assert request=={'instance':'vw'}
            reply=(json.dumps({'ticket':'A'*43,'gameName':'JoinAudit','gameUuid':'unused','expiresAt':'unused'})+'\n').encode()
            if not k.WriteFile(handle,reply,len(reply),ctypes.byref(count),None): raise ctypes.WinError(ctypes.get_last_error())
            k.FlushFileBuffers(handle)
        except Exception as error: errors.append(str(error))
        finally: k.DisconnectNamedPipe(handle); k.CloseHandle(handle)
    thread=threading.Thread(target=serve,daemon=True); thread.start()
    output=run([java,'-Dmojin.join.pipe='+pipe,'-Dmojin.join.instance=vw','-javaagent:'+str(jar),'-cp',classpath,'JoinPipeCheck'])
    thread.join(5)
    assert 'PIPE_PASS' in output and not errors,output+str(errors)
    print('PIPE_PASS',Path(java).parent.parent.name)
    results.append({'runtime':str(java),'ipc':'pass'})

class ApiHandler(http.server.BaseHTTPRequestHandler):
    consumed=set()
    calls=0
    def log_message(self,*args): pass
    def do_POST(self):
        body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        name=body['gameName']; token=body['ticket']
        if token[-1] in 'ir0':
            self.send_response(503); self.end_headers(); return
        allowed=self.headers.get('Authorization')=='Bearer test-secret-0123456789abcdef' and name in ('JoinAudit','WrongUuid') and len(token)==43 and token not in self.consumed
        self.consumed.add(token); type(self).calls+=1
        ident=uuid.UUID(bytes=hashlib.md5(('OfflinePlayer:'+name).encode()).digest(),version=3) if name!='WrongUuid' else uuid.uuid4()
        response=json.dumps({'allowed':allowed,'gameName':name,'gameUuid':str(ident)}).encode()
        self.send_response(200); self.send_header('Content-Length',str(len(response))); self.end_headers(); self.wfile.write(response)
api=http.server.ThreadingHTTPServer(('127.0.0.1',0),ApiHandler); port=api.server_address[1]
threading.Thread(target=api.serve_forever,daemon=True).start()
config=work/'server.properties'
config.write_text(f'mode=enforce\ninstance=vw\nredeemUrl=http://127.0.0.1:{port}/redeem\nsecret=test-secret-0123456789abcdef\n',encoding='utf-8')
for java in javas:
    ApiHandler.consumed.clear(); ApiHandler.calls=0
    output=run([java,'-Dmojin.join.server.config='+str(config),'-javaagent:'+str(jar),'-cp',classpath,'JoinGateCheck',str(port)])
    assert 'GATE_PASS' in output,output
    assert ApiHandler.calls>=6
    print(output.strip())
    results.append({'runtime':str(java),'preLoginNettyGate':'pass'})
api.shutdown(); api.server_close()
report={'jarSha256':hashlib.sha256(jar.read_bytes()).hexdigest(),'results':results,'scope':'Actual Java 8/17/25 IPC, HTTP and Netty; mapped packet fixtures. Four live servers require rollout validation.'}
(work/'result.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('JOIN_COMPONENT_CHECK_PASS')
