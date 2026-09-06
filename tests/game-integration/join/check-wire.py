"""Execute fixed Forge/Cleanroom handshake bytecode from wire bytes through the gate."""
from pathlib import Path
import hashlib, json, os, subprocess
root=Path(__file__).resolve().parents[3]
work=root/'.local/join-agent/wire-check';work.mkdir(parents=True,exist_ok=True)
jb=next((root.parent/'.tools/temurin25').glob('*/bin'))
agent=root/'src/GameIntegration/join/mojin-join-server-agent.jar'
netty=root/'.local/loading-live-20260906/instances/vw/libraries/io/netty/netty-all/4.0.10.Final/netty-all-4.0.10.Final.jar'
asm=list((root/'.local/loading-live-20260906/instances/mb/libraries/org/ow2/asm').glob('**/9.10.1/*.jar'))
cp=os.pathsep.join(map(str,[work,agent,root/'.local/join-agent/check/classes',netty,*asm]))
sources={
'net/minecraft/network/INetHandler.java':'package net.minecraft.network; public interface INetHandler {}',
'net/minecraft/network/handshake/INetHandlerHandshakeServer.java':'package net.minecraft.network.handshake; public interface INetHandlerHandshakeServer extends net.minecraft.network.INetHandler {}',
'net/minecraft/network/EnumConnectionState.java':'package net.minecraft.network; public enum EnumConnectionState { STATUS,LOGIN; public static EnumConnectionState a(int x){return x==2?LOGIN:STATUS;} public int a(){return this==LOGIN?2:1;} }',
'net/minecraft/network/ConnectionProtocol.java':'package net.minecraft.network; public enum ConnectionProtocol { STATUS,LOGIN; public static ConnectionProtocol m_129583_(int x){return x==2?LOGIN:STATUS;} public int m_129582_(){return this==LOGIN?2:1;} }',
'net/minecraft/network/PacketBuffer.java':'''package net.minecraft.network; import java.io.*; import java.nio.charset.StandardCharsets; public class PacketBuffer { final ByteArrayInputStream bytes; public PacketBuffer(byte[] data){bytes=new ByteArrayInputStream(data);} public int g(){int result=0;for(int n=0;n<5;n++){int b=bytes.read();if(b<0)throw new IllegalStateException();result|=(b&127)<<(7*n);if((b&128)==0)return result;}throw new IllegalStateException();} public String e(int max){int n=g();byte[]b=new byte[n];if(bytes.read(b,0,n)!=n)throw new IllegalStateException();return new String(b,StandardCharsets.UTF_8);} public int readUnsignedShort(){return (bytes.read()<<8)|bytes.read();} public PacketBuffer d(int x){return this;} public PacketBuffer a(String s){return this;} public io.netty.buffer.ByteBuf writeShort(int x){return null;} }''',
'net/minecraft/network/FriendlyByteBuf.java':'''package net.minecraft.network; public class FriendlyByteBuf extends PacketBuffer { public FriendlyByteBuf(byte[] b){super(b);} public int m_130242_(){return g();} public String m_130136_(int max){return e(max);} public FriendlyByteBuf m_130130_(int x){return this;} public FriendlyByteBuf m_130070_(String s){return this;} public io.netty.buffer.ByteBuf writeShort(int x){return null;} }''',
'net/minecraft/network/PacketListener.java':'package net.minecraft.network; public interface PacketListener {}',
'net/minecraft/network/protocol/handshake/ServerHandshakePacketListener.java':'package net.minecraft.network.protocol.handshake; public interface ServerHandshakePacketListener extends net.minecraft.network.PacketListener {}',
'net/minecraft/WorldVersion.java':'package net.minecraft; public interface WorldVersion {int m_132495_();}',
'net/minecraft/SharedConstants.java':'package net.minecraft; public class SharedConstants {public static WorldVersion m_183709_(){return ()->763;}}',
'net/minecraftforge/network/NetworkHooks.java':'package net.minecraftforge.network; public class NetworkHooks {public static String getFMLVersion(String host){for(String s:host.split("\\0"))if(s.startsWith("FML"))return s;return "NONE";}}',
}
files=[]
for name,source in sources.items():
 p=work/'src'/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(source,encoding='utf-8');files.append(p)
def run(args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',creationflags=subprocess.CREATE_NO_WINDOW)
 if p.returncode:raise RuntimeError(p.stdout+p.stderr)
 print(p.stdout.strip())
 return p.stdout.strip()
run([jb/'javac.exe','-encoding','UTF-8','-cp',cp,'-d',work,*files,Path(__file__).with_name('WireDecodeCheck.java')])
base=root/'.local/join-agent/decode-inspect'
sockets=root/'.local/jp';sockets.mkdir(exist_ok=True)
inputs=[base/'mb-server-patched.class',base/'server-ClientIntentionPacket.class',base/'dc2.class']
output=run([jb/'java.exe','-Djdk.net.unixdomain.tmpdir='+str(sockets),'-Xverify:all','-cp',cp,'WireDecodeCheck',*inputs])
(work/'result.json').write_text(json.dumps({'passed':True,'serverAgentSha256':hashlib.sha256(agent.read_bytes()).hexdigest(),'checks':['real wire bytes to actual decoded packet','custom ticket removed before Forge hostname split','FML and FML3 preserved','HTTP redemption once before login delivery','no ticket denied before login'],'classSha256':{x.name:hashlib.sha256(x.read_bytes()).hexdigest() for x in inputs},'output':output},indent=2)+'\n',encoding='utf-8')
