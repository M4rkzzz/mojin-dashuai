"""Publish a standalone support ZIP; never change launcher/catalog update pointers."""
import argparse, hashlib, json, pathlib, re, subprocess, sys, urllib.request, uuid, zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('archive', type=pathlib.Path)
args = parser.parse_args()
source = args.archive.resolve()
if not source.is_relative_to((ROOT/'artifacts/diagnostics').resolve()) or source.is_symlink():
    raise ValueError('Expected a built diagnostic archive')
record = json.loads(pathlib.Path(str(source)+'.json').read_text(encoding='utf-8-sig'))
build = record['buildId']
if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9.-]+', build) or source.name != f'MojinDashuai-{build}-x64.zip':
    raise ValueError('Invalid diagnostic archive identity')
with source.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
if digest != record['sha256'] or source.stat().st_size != record['bytes']:
    raise ValueError('Diagnostic archive differs from its build record')
with zipfile.ZipFile(source) as archive:
    names=archive.namelist()
    if record.get('kind')=='startup-apphost-patch':
        if set(names)!={'app/MojinDashuai.Launcher.exe','使用说明.txt','patch.json'}:
            raise ValueError('Unexpected file in startup host patch')
        patch=json.loads(archive.read('patch.json'))
        if patch['buildId']!=build or patch['baseBuild']!=record['baseBuild'] or patch['path']!='app/MojinDashuai.Launcher.exe':
            raise ValueError('Startup patch identity differs')
        if hashlib.sha256(archive.read(patch['path'])).hexdigest()!=patch['newSha256']:
            raise ValueError('Startup patch executable hash differs')
    else:
        for needed in ('diagnostics/run.ps1', 'app/MojinDashuai.Launcher.exe', '启动诊断.cmd', '兼容模式诊断.cmd', '收集日志.cmd'):
            if not any(name.replace('\\','/').endswith('/'+needed) for name in names):
                raise ValueError('Diagnostic archive lacks its run or collection entry point')
    if any('/profile/' in name.lower().replace('\\','/') or name.lower().endswith(('/settings.json','/account.bin','/startup.jsonl','/dotnet-host.txt')) for name in names):
        raise ValueError('Diagnostic archive includes runtime private data')
run=uuid.uuid4().hex
remote='/tmp/mojin-startup-debug-'+run
stage=ROOT/'.local/diagnostic-publication';stage.mkdir(parents=True,exist_ok=True)
script=stage/(run+'.py')
relative='diagnostics/startup/'+build+'/'+source.name
script.write_text('''import hashlib,json,pathlib,shutil
source=pathlib.Path(SOURCE)
root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
target=root/RELATIVE
if target.is_symlink() or not target.resolve().is_relative_to(root):raise ValueError('Invalid support download path')
with source.open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
if digest!=HASH or source.stat().st_size!=SIZE:raise ValueError('Uploaded support archive differs')
target.parent.mkdir(parents=True,exist_ok=True)
if target.exists():
 with target.open('rb') as f:existing=hashlib.file_digest(f,'sha256').hexdigest()
 if existing!=digest:raise ValueError('Existing diagnostic bytes are immutable')
else:
 temporary=target.with_name(target.name+'.stage-'+RUN)
 shutil.copyfile(source,temporary);temporary.chmod(0o644);temporary.replace(target)
source.unlink()
print(json.dumps({'published':True,'automaticUpdateChanged':False}))
'''.replace('SOURCE',repr(remote+'.zip')).replace('RELATIVE',repr(relative)).replace('HASH',repr(digest)).replace('SIZE',str(source.stat().st_size)).replace('RUN',repr(run)),encoding='utf-8')
def ssh(*args):
    result=subprocess.run([sys.executable,str(ROOT.parent/'tools/ssh124.py'),'--user','Agent2',*args],capture_output=True,timeout=180)
    if result.returncode:raise RuntimeError('Support ZIP publication failed; credentials were not printed')
    return result.stdout.decode('utf-8',errors='replace').strip()
ssh('--send',str(source),remote+'.zip')
ssh('--send',str(script),remote+'.py')
print(ssh('--sudo','python3 '+remote+'.py'))
url='https://launcher-direct.boshan.uk:21708/'+relative
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
for attempt in range(3):
    try:
        with opener.open(url,timeout=35) as response:
            size=0; actual=hashlib.sha256()
            while block:=response.read(1024*1024):size+=len(block);actual.update(block)
        if size!=record['bytes'] or actual.hexdigest()!=digest:raise ValueError('Public support archive differs')
        break
    except (OSError,TimeoutError):
        if attempt==2:raise
record.update(downloadUrl=url,publicArchiveRoundTripVerified=True,automaticUpdateChanged=False)
(stage/(build+'.json')).write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(json.dumps(record))
