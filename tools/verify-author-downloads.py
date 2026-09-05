"""Verify exact author-hosted mod files; cache by hash and leave pack acceptance closed."""
import argparse, concurrent.futures, datetime, hashlib, json, pathlib, re, threading, time, urllib.error, urllib.parse, urllib.request

root=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--limit-mib',type=float,default=2)
args=parser.parse_args()
if not 0<args.limit_mib<=20:raise SystemExit('Download limit must be between 0 and 20 MiB/s.')
cache=root/'.local/source-cache'
cache.mkdir(parents=True,exist_ok=True)
lock=threading.Lock()
next_read=time.monotonic()
def throttle(size):
    global next_read
    with lock:
        now=time.monotonic()
        next_read=max(now,next_read)+size/(args.limit_mib*1024**2)
        delay=next_read-now
    if delay>0:time.sleep(delay)
class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs):return None
def verify(row):
    sha=row['sha256'];size=row['size']
    if not re.fullmatch('[a-f0-9]{64}',sha) or not isinstance(size,int) or not 0<size<1024**3:raise ValueError('Invalid file record')
    url=row['sources'][0];uri=urllib.parse.urlsplit(url)
    if uri.scheme!='https' or uri.hostname!='cdn.modrinth.com' or uri.username or uri.password or uri.port not in (None,443):raise ValueError('Unexpected author CDN')
    target=cache/(sha+'.jar')
    if target.is_file() and target.stat().st_size==size:
        with target.open('rb') as saved:
            if hashlib.file_digest(saved,'sha256').hexdigest()==sha:return {'verified':True,'cached':True,'at':datetime.datetime.now(datetime.timezone.utc).isoformat()}
    part=target.with_suffix('.part')
    try:
        request=urllib.request.Request(url,headers={'User-Agent':'MojinDashuai-ReleaseVerifier/0.1','Accept-Encoding':'identity'})
        with urllib.request.build_opener(NoRedirect).open(request,timeout=30) as response,part.open('wb') as output:
            if response.status!=200:raise ValueError('Unexpected HTTP response')
            digest=hashlib.sha256();count=0
            while chunk:=response.read(65536):
                count+=len(chunk)
                if count>size:raise ValueError('File exceeds expected size')
                digest.update(chunk);output.write(chunk);throttle(len(chunk))
            if count!=size or digest.hexdigest()!=sha:raise ValueError('File verification failed')
        part.replace(target)
        return {'verified':True,'cached':False,'at':datetime.datetime.now(datetime.timezone.utc).isoformat()}
    except Exception as error:
        part.unlink(missing_ok=True)
        return {'verified':False,'error':type(error).__name__,'httpStatus':error.code if isinstance(error,urllib.error.HTTPError) else None}

audits={name:json.loads((root/f'packs/{name}-source-audit.json').read_text(encoding='utf-8-sig')) for name in ['m3e','mb']}
jobs={}
for audit in audits.values():
    for row in audit['files']:
        if row.get('originEvidence',{}).get('provider')=='modrinth':jobs.setdefault(row['sha256'],row)
results={}
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    futures={pool.submit(verify,row):sha for sha,row in jobs.items()}
    for future in concurrent.futures.as_completed(futures):
        results[futures[future]]=future.result()
        if len(results)%25==0:print(json.dumps({'checked':len(results),'total':len(jobs),'passed':sum(r['verified'] for r in results.values())}),flush=True)
for name,audit in audits.items():
    for row in audit['files']:
        if row['sha256'] in results:
            row['downloadVerification']=results[row['sha256']]
            if results[row['sha256']]['verified']:row['status']='author-origin-download-verified'
    audit['releaseReady']=False
    (root/f'packs/{name}-source-audit.json').write_text(json.dumps(audit,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'checked':len(results),'passed':sum(r['verified'] for r in results.values()),'failed':sum(not r['verified'] for r in results.values()),'releaseReady':False}))
