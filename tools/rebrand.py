from pathlib import Path
root=Path(__file__).resolve().parents[1]
for relative in ('ui/src/main.tsx','ui/index.html','src/Launcher.Desktop/MainWindow.xaml','src/Launcher.Desktop/app.manifest','src/Launcher.Desktop/Launcher.Desktop.csproj','src/Launcher.Core/GameLauncher.cs'):
    path=root/relative
    text=path.read_text(encoding='utf-8-sig')
    for old,new in [('泊山 · 群服大厅','魔金大帅'),('BOSHAN LAUNCHER','魔金大帅'),('BOSHAN WORLDS','三服统一客户端'),('WELCOME TO BOSHAN','WELCOME BACK'),('>泊山<','>魔金大帅<'),('Boshan.Launcher</AssemblyName>','MojinDashuai.Launcher</AssemblyName>'),('name="Boshan.Launcher"','name="MojinDashuai.Launcher"'),('GameLauncherName="BoshanLauncher"','GameLauncherName="MojinDashuai"')]:
        text=text.replace(old,new)
    path.write_text(text,encoding='utf-8')
