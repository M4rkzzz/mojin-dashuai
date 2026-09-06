from pathlib import Path
import http.server,threading,secrets,json,subprocess
root=Path(__file__).resolve().parents[3];out=root/'.local/activities/boot';out.mkdir(parents=True,exist_ok=True);(out/'playerdata').mkdir(exist_ok=True)
key=secrets.token_hex(32);catalog=json.loads((root/'activities/catalog.json').read_text(encoding='utf-8'))
class Handler(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  if self.headers.get('Authorization')!='Bearer '+key:self.send_error(403);return
  w=next((w for w in catalog['worlds'] if self.path=='/internal/v1/activities/'+w['id']+'/definition'),None)
  if w is None:self.send_error(404);return
  self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(json.dumps(w).encode())
 def log_message(self,*args):pass
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
java25=next((root.parent/'.tools/temurin25').glob('*/bin'));java8=next((root.parent/'_voidwayfarer4-deploy/angelica-local-lab-20260906').glob('*/runtimes/*/*/bin/java.exe'))
(out/'ActivityBoot.java').write_text('''public final class ActivityBoot {public static void main(String[] a)throws Exception{Class<?> c=Class.forName("uk.boshan.activities.ActivityRuntime",false,null);java.lang.reflect.Field f=c.getDeclaredField("definition");f.setAccessible(true);for(int n=0;n<100&&f.get(null)==null;n++)Thread.sleep(100);if(f.get(null)==null)throw new AssertionError("definition missing");System.out.println("BOOT_PASS "+System.getProperty("java.version"));}}''')
def run(args):
 p=subprocess.run(list(map(str,args)),cwd=out,capture_output=True,text=True,encoding='utf-8',errors='replace',creationflags=0x08000000,timeout=25)
 if p.returncode:raise RuntimeError(p.stdout+p.stderr)
 print(p.stdout.strip())
try:
 run([java25/'javac.exe','--release','8','-d',out,out/'ActivityBoot.java'])
 for world,java in [('m3e',java8),('mb',java25/'java.exe')]:
  cfg=out/(world+'.properties');cfg.write_text('instance='+world+'\nsecret='+key+'\nbaseUrl=http://127.0.0.1:'+str(server.server_port)+'/internal/v1/activities/'+world+'\nplayerDataDirectory='+str(out/'playerdata').replace('\\','/')+'\nspoolDirectory='+str(out/world).replace('\\','/')+'\n')
  run([java,'-javaagent:'+str(root/'artifacts/activities/mojin-activities-server-agent.jar'),'-Dmojin.activities.config='+str(cfg),'-cp',out,'ActivityBoot'])
finally:server.shutdown()
