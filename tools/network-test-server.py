"""Inside gsmanager: prepare/run only an isolated DC2 copy, never restart production."""
from pathlib import Path
import argparse,json,os,shutil,signal,socket,subprocess,time

SOURCE=Path('/home/steam/games/DeceasedCraft-2')
ROOT=Path('/home/steam/games/.mojin-network-test-dc2')
def process():
    p=ROOT/'process.json'
    if not p.exists():return None
    pid=json.loads(p.read_text())['pid']
    try:
        return pid if Path(os.readlink(f'/proc/{pid}/cwd')).resolve()==ROOT.resolve() else None
    except OSError:return None
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('action',choices=['prepare','start','stop','status']);parser.add_argument('--zstd',action='store_true');args=parser.parse_args()
    if ROOT.resolve().parent!=SOURCE.parent.resolve() or ROOT.name!='.mojin-network-test-dc2':raise RuntimeError('Invalid isolated root')
    if args.action=='prepare':
        if ROOT.exists():raise RuntimeError('Test copy already exists; refusing overwrite')
        for port in (25512,25513):
            with socket.socket() as s:s.bind(('0.0.0.0',port))
        ROOT.mkdir();(ROOT/'.network-trial').write_text('dc2-isolated-v1')
        for name in ('libraries','mods','config','defaultconfigs','kubejs','generated_datapacks','patchouli_books','tlm_custom_pack','customnpcs','local','tacz','schematics'):
            if (SOURCE/name).exists():subprocess.run(['cp','-a','--reflink=auto',str(SOURCE/name),str(ROOT/name)],check=True)
        shutil.copy2(SOURCE/'server.properties',ROOT/'server.properties.baseline')
        props={}
        for line in (SOURCE/'server.properties').read_text().splitlines():
            if '=' in line and not line.startswith('#'):k,v=line.split('=',1);props[k]=v
        props.update({'server-port':'25512','server-ip':'127.0.0.1','level-name':'network-test-world','level-type':'minecraft:flat','max-players':'4','white-list':'false','enforce-whitelist':'false','enable-rcon':'false','enable-query':'false','online-mode':'false','network-compression-threshold':'256','motd':'Mojin isolated network trial','spawn-protection':'0'})
        (ROOT/'server.properties').write_text('\n'.join(k+'='+v for k,v in props.items())+'\n')
        (ROOT/'eula.txt').write_text('eula=true\n');(ROOT/'user_jvm_args.txt').write_text('-Xms1G\n-Xmx6G\n')
        voice=ROOT/'config/voicechat/voicechat-server.properties'
        if voice.exists():
            import re
            voice.write_text(re.sub(r'^port=.*$','port=25516',voice.read_text(),flags=re.M))
        (ROOT/'.join-auth').mkdir();shutil.copy2(SOURCE/'.join-auth/server.properties',ROOT/'.join-auth/server.properties')
        shutil.copy2('/tmp/mojin-network-join-agent.jar',ROOT/'.join-auth/agent.jar')
        print(json.dumps({'prepared':str(ROOT),'productionChanged':False}));return
    if not (ROOT/'.network-trial').is_file():raise RuntimeError('Missing test marker')
    if args.action=='start':
        if process():raise RuntimeError('Test server already running')
        mod=ROOT/'mods/zstdnet-1.20.1-forge-1.4.8-mojin.1.jar'
        if args.zstd:
            shutil.copy2('/tmp/mojin-network-zstdnet.jar',mod);shutil.copy2('/tmp/mojin-network-zstdnet.properties',ROOT/'config/zstdnet-server.properties')
        elif mod.exists():mod.unlink()
        # The baseline uses the exact same test world and game config, apart from compression.
        props=ROOT/'server.properties';text=props.read_text();import re
        text=re.sub(r'^network-compression-threshold=.*$','network-compression-threshold=256',text,flags=re.M)
        text=re.sub(r'^server-port=.*$','server-port=25512',text,flags=re.M);props.write_text(text)
        text=re.sub(r'^server-ip=.*$','server-ip='+('127.0.0.1' if args.zstd else '0.0.0.0'),text,flags=re.M);props.write_text(text)
        command=['/usr/lib/jvm/temurin-21-jdk-amd64/bin/java','-javaagent:'+str(ROOT/'.join-auth/agent.jar'),'-Dmojin.join.server.config='+str(ROOT/'.join-auth/server.properties'),'@user_jvm_args.txt','@libraries/net/minecraftforge/forge/1.20.1-47.4.0/unix_args.txt','nogui']
        with (ROOT/('zstd-console.log' if args.zstd else 'baseline-console.log')).open('w') as output:
            p=subprocess.Popen(command,cwd=ROOT,stdin=subprocess.DEVNULL,stdout=output,stderr=subprocess.STDOUT,start_new_session=True)
        (ROOT/'process.json').write_text(json.dumps({'pid':p.pid,'zstd':args.zstd,'at':time.time()}));print(json.dumps({'started':p.pid,'zstd':args.zstd}));return
    pid=process()
    if args.action=='stop' and pid:
        os.kill(pid,signal.SIGTERM);print(json.dumps({'stoppingTestPid':pid}));return
    print(json.dumps({'pid':pid,'process':json.loads((ROOT/'process.json').read_text()) if (ROOT/'process.json').exists() else None}))
if __name__=='__main__':main()
