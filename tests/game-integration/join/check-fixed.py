from pathlib import Path
import os, subprocess

root = Path(__file__).resolve().parents[3]
base = root / '.local/loading-live-20260906/instances'
java = next((root.parent / '.tools/temurin25').glob('*/bin'))
classes = root / '.local/join-agent/check/classes'
classpath = os.pathsep.join(map(str, [classes,root/'src/GameIntegration/join/mojin-join-agent.jar', *base.joinpath('mb/libraries/org/ow2/asm').glob('**/9.10.1/*.jar')]))
def run(args):
    r = subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',creationflags=subprocess.CREATE_NO_WINDOW)
    if r.returncode: raise RuntimeError(r.stdout+r.stderr)
    print(r.stdout.strip())
run([java/'javac.exe','-J-Duser.language=en','--release','8','-Xlint:-options','-cp',classpath,'-d',classes,Path(__file__).with_name('FixedClassCheck.java')])
legacy = next(base.joinpath('vw/versions').glob('*/*.jar'))
cleanroom = next(base.joinpath('mb/versions').glob('*/*.jar'))
modern = next(base.joinpath('dc2/libraries/net/minecraft/client').glob('*/*-srg.jar'))
run([java/'java.exe','-cp',classpath,'FixedClassCheck',
 'net/minecraft/network/NetworkManager',legacy,'ej.class',
 'net/minecraft/network/handshake/client/C00Handshake',legacy,'jp.class',
 'net/minecraft/network/NetworkManager',cleanroom,'gw.class',
 'net/minecraft/network/handshake/client/C00Handshake',cleanroom,'md.class',
 'net/minecraft/network/Connection',modern,'net/minecraft/network/Connection.class',
 'net/minecraft/network/protocol/handshake/ClientIntentionPacket',modern,'net/minecraft/network/protocol/handshake/ClientIntentionPacket.class'])
