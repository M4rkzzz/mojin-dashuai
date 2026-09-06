"""Run a fresh-world VW network smoke inside gsmanager, separate from production."""
from pathlib import Path
import argparse,json,os,shutil,signal,socket,subprocess,time
ROOT=Path('/home/steam/games/.mojin-network-test-vw')
SOURCE=Path('/home/steam/games/VoidWayfarer-4')
def process():
    if not (ROOT/'process.json').exists():return None
    pid=json.loads((ROOT/'process.json').read_text())['pid']
    try:return pid if Path(os.readlink(f'/proc/{pid}/cwd')).resolve()==ROOT else None
    except OSError:return None
def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('action',choices=['prepare','start','stop','status']);a=p.parse_args()
    if ROOT.resolve().parent!=SOURCE.parent or ROOT.name!='.mojin-network-test-vw':raise RuntimeError('Unexpected test root')
    if a.action=='prepare':
        if ROOT.exists():raise RuntimeError('Test directory already exists')
        with socket.socket() as s:s.bind(('0.0.0.0',25514))
        ROOT.mkdir();(ROOT/'.network-trial').write_text('vw-isolated-v1')
        for name in ('mods','config','scripts','resources','libraries','defaultconfigs'):
            if (SOURCE/name).exists():subprocess.run(['cp','-a','--reflink=auto',str(SOURCE/name),str(ROOT/name)],check=True)
        for name in ('minecraft_server.1.7.10.jar','forge-1.7.10-10.13.4.1614-1.7.10-universal.jar','gregtech.cfg','gregtech.lang'):
            if (SOURCE/name).exists():shutil.copy2(SOURCE/name,ROOT/name)
        props={}
        for line in (SOURCE/'server.properties').read_text().splitlines():
            if '=' in line and not line.startswith('#'):k,v=line.split('=',1);props[k]=v
        props.update({'server-port':'25514','server-ip':'0.0.0.0','level-name':'network-test-world','level-type':'FLAT','max-players':'4','white-list':'false','enable-rcon':'false','enable-query':'false','online-mode':'false','motd':'Mojin isolated legacy smoke','spawn-protection':'0'})
        (ROOT/'server.properties').write_text('\n'.join(k+'='+v for k,v in props.items())+'\n');(ROOT/'eula.txt').write_text('eula=true\n')
        (ROOT/'.join-auth').mkdir();shutil.copy2(SOURCE/'.join-auth/server.properties',ROOT/'.join-auth/server.properties')
        shutil.copy2('/tmp/mojin-network-join-agent.jar',ROOT/'.join-auth/agent.jar')
        shutil.copy2('/tmp/mojin-legacy-network.jar',ROOT/'mods/mojin-legacy-network-1.7.10-1.0.0.jar')
        print(json.dumps({'prepared':str(ROOT),'productionChanged':False}));return
    if not (ROOT/'.network-trial').is_file():raise RuntimeError('Missing marker')
    pid=process()
    if a.action=='start':
        if pid:raise RuntimeError('Already running')
        command=['/home/.local/java8-vw4/bin/java','-javaagent:'+str(ROOT/'.join-auth/agent.jar'),'-Dmojin.join.server.config='+str(ROOT/'.join-auth/server.properties'),'-Xms512M','-Xmx3G','-Dfile.encoding=UTF-8','-Dfml.readTimeout=180','-jar','forge-1.7.10-10.13.4.1614-1.7.10-universal.jar','nogui']
        with (ROOT/'console.log').open('w') as output:
            child=subprocess.Popen(command,cwd=ROOT,stdin=subprocess.DEVNULL,stdout=output,stderr=subprocess.STDOUT,start_new_session=True)
        (ROOT/'process.json').write_text(json.dumps({'pid':child.pid,'at':time.time()}));print(json.dumps({'started':child.pid}));return
    if a.action=='stop' and pid:os.kill(pid,signal.SIGTERM)
    print(json.dumps({'pid':pid,'action':a.action}))
if __name__=='__main__':main()
