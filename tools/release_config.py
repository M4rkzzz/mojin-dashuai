import hashlib, pathlib

ROOT=pathlib.Path(__file__).resolve().parents[1]
AUTH_FILES=[
    'src/Launcher.Desktop/Accounts.cs','src/Launcher.Desktop/WindowsMicrosoftLogin.cs',
    'src/Launcher.Desktop/MicrosoftWebUi.cs','src/Launcher.Desktop/MicrosoftOAuthWebUi.cs',
    'src/Launcher.Desktop/MicrosoftAccountStorage.cs','src/Launcher.Desktop/Vault.cs',
    'src/Launcher.Desktop/launcher.json','src/Launcher.Desktop/Launcher.Desktop.csproj',
    'src/Launcher.Desktop/packages.lock.json','src/Launcher.Core/MinecraftAuthentication.cs',
]
def auth_fingerprint():
    digest=hashlib.sha256()
    for name in AUTH_FILES:
        digest.update(name.encode()+b'\0')
        digest.update((ROOT/name).read_bytes().replace(b'\r\n',b'\n'))
        digest.update(b'\0')
    return digest.hexdigest()
