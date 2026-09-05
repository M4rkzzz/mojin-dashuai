"""Exercise the installed game engines under a Unicode path without opening games."""
import argparse,json,os,pathlib,shutil,subprocess,uuid

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('installed_root',type=pathlib.Path)
args=parser.parse_args()
repo=pathlib.Path(__file__).resolve().parents[1]
source=args.installed_root.resolve()
root=repo/'.local'/'路径验证'/uuid.uuid4().hex/'魔金大帅 with spaces'/'content'
root.mkdir(parents=True)
def copy_tree(src,dst):
    for file in src.rglob('*'):
        if not file.is_file():continue
        target=dst/file.relative_to(src);target.parent.mkdir(parents=True,exist_ok=True)
        # Only immutable runtimes/libraries/version files are shared. Player files are never linked.
        os.link(file,target)
copy_tree(source/'runtimes',root/'runtimes')
for instance in ('m3e','dc2','mb'):
    old=source/'instances'/instance;new=root/'instances'/instance
    for directory in ('libraries','versions'):copy_tree(old/directory,new/directory)
    (new/'.hub').mkdir(parents=True,exist_ok=True)
    shutil.copy2(old/'.hub/installed.json',new/'.hub/installed.json')
(root/'probe').mkdir()
javac=next((root/'runtimes').rglob('javac.exe'))
subprocess.run([str(javac),'--release','8','-Xlint:-options','-encoding','UTF-8','-d',str(root/'probe'),str(repo/'tests/NativeAccountSmoke/ContentPathProbe.java')],check=True)
dotnet=repo.parent/'.tools/dotnet10/dotnet.exe'
subprocess.run([str(dotnet),'build',str(repo/'tests/NativeAccountSmoke/NativeAccountSmoke.csproj'),'-c','Release'],check=True,cwd=repo)
subprocess.run([str(dotnet),str(repo/'tests/NativeAccountSmoke/bin/Release/net10.0-windows/NativeAccountSmoke.dll'),'--content-path-smoke',str(root)],check=True,cwd=repo)
report=json.loads((root/'path-report.json').read_text(encoding='utf-8-sig'))
(repo/'packs/content-path-acceptance.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
