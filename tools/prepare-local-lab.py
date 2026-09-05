"""Prepare isolated local test copies; never modify the supplied client or production servers."""
from pathlib import Path
import shutil,json,hashlib,zipfile

root=Path(__file__).resolve().parents[1]
source=Path(r'D:\Desktop\【三服】肉丸工艺\dmeatball\.minecraft')
instance=source/'versions/Meatballcraft'
target=root/'.local/lab/mb'
target.mkdir(parents=True,exist_ok=True)
for directory in ('assets','libraries'):
    shutil.copytree(source/directory,target/directory,dirs_exist_ok=True)
for directory in ('mods','config','scripts','resources','structures','patchouli_books','memory_repo','resourcepacks'):
    original=instance/directory
    if original.exists(): shutil.copytree(original,target/directory,dirs_exist_ok=True)
version=target/'versions/1.12.2'
version.mkdir(parents=True,exist_ok=True)
# Prepare an official vanilla parent, not the old Forge profile.
import urllib.request
index=json.load(urllib.request.urlopen('https://piston-meta.mojang.com/mc/game/version_manifest_v2.json'))
meta=next(v for v in index['versions'] if v['id']=='1.12.2')
data=json.load(urllib.request.urlopen(meta['url']))
(version/'1.12.2.json').write_text(json.dumps(data),encoding='utf-8')
shutil.copy2(instance/'Meatballcraft.jar',version/'1.12.2.jar')
assert hashlib.sha1((version/'1.12.2.jar').read_bytes()).hexdigest()==data['downloads']['client']['sha1']
(target/'launcher_profiles.json').write_text('{"profiles":{}}')
(target/'options.txt').write_text('lang:zh_cn\nfullscreen:false\n',encoding='utf-8')
# This machine must not be reintroduced: production intentionally removed its registration.
machine=target/'config/modularmachinery/machinery/induction_electrolyzer.json'
if machine.exists():
    disabled=target/'.hub/compatibility-disabled';disabled.mkdir(parents=True,exist_ok=True)
    shutil.move(str(machine),str(disabled/machine.name))
files=[]
for path in sorted((target/'mods').glob('*.jar')):
    files.append({'name':path.name,'size':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
(root/'.local/mb-local-mods.json').write_text(json.dumps(files,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'testDirectory':str(target),'mods':len(files),'sourceModified':False,'personalDataImported':False},ensure_ascii=False))
