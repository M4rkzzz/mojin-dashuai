"""Inventory mod sources without copying private client files into the release tree."""
from pathlib import Path
import json,hashlib,zipfile,urllib.request,concurrent.futures,time
root=Path(__file__).resolve().parents[1]
work=root.parent
packs={
 'm3e':(Path(r'D:\Desktop\魔金大帅\.minecraft\versions\MSE'),None),
 'dc2':(None,work/'DeceasedCraft-2-client-zh-5.10.17/manifest.json'),
 'mb':(Path(r'D:\Desktop\【三服】肉丸工艺\dmeatball\.minecraft\versions\Meatballcraft'),None)
}
for id,(instance,manifest_path) in packs.items():
    if instance is not None and (instance/'manifest.json').exists(): manifest_path=instance/'manifest.json'
    manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig')) if manifest_path and manifest_path.exists() else {}
    refs={f.get('fileName',''):f for f in manifest.get('files',[])}
    rows=[]
    if instance:
        for file in sorted((instance/'mods').glob('*.jar')):
            data=file.read_bytes();reference=refs.get(file.name,{})
            row={'path':'mods/'+file.name,'size':len(data),'sha256':hashlib.sha256(data).hexdigest(),'sha1':hashlib.sha1(data).hexdigest(),'sources':[reference['url']] if reference.get('url') else [],'projectId':reference.get('projectID'),'fileId':reference.get('fileID'),'distributionBasis':None,'status':'needs-source-and-license-review'}
            try:
                with zipfile.ZipFile(file) as jar:
                    license_files=[n for n in jar.namelist() if n.lower().split('/')[-1].startswith(('license','copying')) and not n.endswith('/')]
                    row['embeddedLicenseFiles']=license_files[:8]
            except zipfile.BadZipFile: row['status']='invalid-jar'
            rows.append(row)
    else:
        rows=[{'projectId':f['projectID'],'fileId':f['fileID'],'required':f.get('required',True),'status':'needs-official-file-metadata','sources':[],'distributionBasis':None} for f in manifest.get('files',[])]
    target=root/'packs'/f'{id}-source-audit.json'
    target.write_text(json.dumps({'instance':id,'releaseReady':False,'files':rows},ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'instance':id,'files':len(rows),'knownDownloadUrls':sum(bool(r['sources']) for r in rows),'releaseReady':False}))
