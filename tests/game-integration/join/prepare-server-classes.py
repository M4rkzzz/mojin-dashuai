"""Recreate fixed decoder bytecode from read-only NAS fixture JAR copies.

Inputs under .local/join-agent: mb-server-cleanroom.jar (0.5.14-alpha),
mb-server-vanilla.jar (1.12.2), dc2-server-srg.jar (1.20.1-20230612.114412).
These are copied from the actual server, never downloaded from an author fallback.
"""
from pathlib import Path
import json, lzma, os, struct, subprocess, zipfile, zlib

root=Path(__file__).resolve().parents[3]
work=root/'.local/join-agent/decode-inspect';work.mkdir(parents=True,exist_ok=True)
cache=root/'.local/join-agent'
java8=json.loads((root/'.local/loading-live-20260906/prepared.json').read_text(encoding='utf-8'))['vw']['java']
jb=next((root.parent/'.tools/temurin25').glob('*/bin'))
cleanroom=cache/'mb-server-cleanroom.jar'

def run(args):
 result=subprocess.run(list(map(str,args)),capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
 if result.returncode:raise RuntimeError(result.stderr.decode('utf-8',errors='replace'))

unpack=work/'UnpackJoinPatch.java'
unpack.write_text('import java.io.*;import java.util.jar.*;public class UnpackJoinPatch{public static void main(String[]a)throws Exception{try(InputStream i=new FileInputStream(a[0]);JarOutputStream o=new JarOutputStream(new FileOutputStream(a[1]))){Pack200.newUnpacker().unpack(i,o);}}}',encoding='utf-8')
run([jb/'javac.exe','--release','8','-d',work,unpack])
with zipfile.ZipFile(cleanroom) as archive:(work/'server-binpatch.pack').write_bytes(lzma.decompress(archive.read('binpatches.pack.lzma')))
run([java8,'-cp',work,'UnpackJoinPatch',work/'server-binpatch.pack',work/'server-binpatch.jar'])
with zipfile.ZipFile(work/'server-binpatch.jar') as archive:patch=archive.read('binpatch/server/net.minecraft.network.handshake.client.C00Handshake.binpatch')
assert patch[0]==1
position=1;names=[]
for _ in range(2):
 length=struct.unpack('>H',patch[position:position+2])[0];position+=2;names.append(patch[position:position+length].decode('utf-8'));position+=length
assert names==['md','net/minecraft/network/handshake/client/C00Handshake'] and patch[position]==1
position+=1;checksum=struct.unpack('>I',patch[position:position+4])[0];position+=4
with zipfile.ZipFile(cache/'mb-server-vanilla.jar') as archive:original=archive.read('md.class')
assert zlib.adler32(original)==checksum,'Server binpatch source checksum mismatch'
length=struct.unpack('>I',patch[position:position+4])[0];position+=4
(work/'md.original.class').write_bytes(original);(work/'md.gdiff').write_bytes(patch[position:position+length])
apply=work/'ApplyJoinPatch.java'
apply.write_text('import java.nio.file.*;import net.minecraftforge.fml.repackage.com.nothome.delta.GDiffPatcher;public class ApplyJoinPatch{public static void main(String[]a)throws Exception{Files.write(Paths.get(a[2]),new GDiffPatcher().patch(Files.readAllBytes(Paths.get(a[0])),Files.readAllBytes(Paths.get(a[1]))));}}',encoding='utf-8')
run([jb/'javac.exe','-cp',cleanroom,'-d',work,apply])
run([jb/'java.exe','-cp',str(work)+os.pathsep+str(cleanroom),'ApplyJoinPatch',work/'md.original.class',work/'md.gdiff',work/'mb-server-patched.class'])
with zipfile.ZipFile(cache/'dc2-server-srg.jar') as archive:
 for simple in ['Connection','ClientIntentionPacket']:
  name='net/minecraft/network/'+('protocol/handshake/' if simple=='ClientIntentionPacket' else '')+simple+'.class'
  (work/('server-'+simple+'.class')).write_bytes(archive.read(name))
forge=next((root/'.local/loading-live-20260906/instances/dc2/libraries/net/minecraftforge/forge').glob('*/*-client.jar'))
with zipfile.ZipFile(forge) as archive:(work/'dc2.class').write_bytes(archive.read('net/minecraft/network/protocol/handshake/ClientIntentionPacket.class'))
print('Fixed server decoder fixtures prepared; Cleanroom source checksum verified')
