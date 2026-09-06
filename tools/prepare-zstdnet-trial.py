"""Prepare the pinned ZstdNet trial with authentication-safe logs; never publish or deploy."""
from pathlib import Path
import argparse, hashlib, json, subprocess, zipfile

ROOT=Path(__file__).resolve().parents[1]
def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source',type=Path)
    parser.add_argument('--output',type=Path,default=ROOT/'.local/network-optimization/zstdnet')
    args=parser.parse_args();out=args.output.resolve();out.mkdir(parents=True,exist_ok=True)
    jdk=next((ROOT.parent/'.tools/temurin25').glob('*/bin/javac.exe')).parent
    asm=ROOT/'.local/loading-live-20260906/instances/mb/libraries/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar'
    subprocess.run([str(jdk/'javac.exe'),'--release','8','-cp',str(asm),'-d',str(out),str(ROOT/'tools/ZstdNetPrivacyPatch.java')],check=True,creationflags=subprocess.CREATE_NO_WINDOW)
    target='cn/tohsaka/factory/zstdnet/proxy/LocalZstdNet.class'
    with zipfile.ZipFile(args.source) as jar:
        (out/'original.class').write_bytes(jar.read(target))
        subprocess.run([str(jdk/'java.exe'),'-cp',str(out)+';'+str(asm),'ZstdNetPrivacyPatch',str(out/'original.class'),str(out/'patched.class')],check=True,creationflags=subprocess.CREATE_NO_WINDOW)
        result=out/'zstdnet-1.20.1-forge-1.4.8-mojin.1.jar'
        with zipfile.ZipFile(result,'w',zipfile.ZIP_DEFLATED) as patched:
            for item in jar.infolist():patched.writestr(item,(out/'patched.class').read_bytes() if item.filename==target else jar.read(item))
            patched.writestr('META-INF/MOJIN-PATCHES.txt','Based on ZstdNet 1.4.8 (MIT), wish131400. Only sanitizeHandshakeHost logging output is redacted to protect launcher join tickets. Wire protocol unchanged. Source: tools/ZstdNetPrivacyPatch.java in https://github.com/M4rkzzz/mojin-dashuai\n')
    (out/'zstdnet-client.toml').write_text('level = 3\n',encoding='utf-8')
    (out/'zstdnet-server.properties').write_text('enabled=true\nauto_takeover=true\nlevel=3\nflush_interval=2ms\nmax_rate_per_conn_bps=0\nmax_rate_global_bps=0\nvoice_chat_passthrough=false\nudp_direct_ports=\ntrust_proxy_protocol=false\nmax_req_per_window=0\nstats_interval=30s\n',encoding='utf-8')
    report={'upstream':'https://www.curseforge.com/minecraft/mc-mods/zstdnet/files/8752125','upstreamSha256':hashlib.sha256(args.source.read_bytes()).hexdigest(),'candidate':str(result),'sha256':hashlib.sha256(result.read_bytes()).hexdigest(),'modifiedClass':target,'productionEnabled':False}
    (out/'prepared.json').write_text(json.dumps(report,indent=2),encoding='utf-8');print(json.dumps(report))
if __name__=='__main__':main()
