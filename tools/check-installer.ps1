param(
    [Parameter(Mandatory=$true)][string]$Source,
    [string]$PreviousSource,
    [string]$Version='0.1.2-beta.6',
    [string]$Compiler
)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
$testRoot=Join-Path $repository ('.local\installer-acceptance\'+[guid]::NewGuid().ToString('N'))
$installRoot=Join-Path $testRoot '安装路径 with spaces'
$registryPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MojinDashuai.Installer.Acceptance_is1'
if (Test-Path $registryPath) { throw 'A previous installer acceptance instance exists; inspect before rerunning' }
if (!$PreviousSource) { $PreviousSource=$Source }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$beforeProcesses=@(Get-Process -Name 'MojinDashuai.Launcher','java','javaw' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$oldOutput=Join-Path $testRoot 'old'
$newOutput=Join-Path $testRoot 'new'
& (Join-Path $PSScriptRoot 'build-installer.ps1') -Version '0.0.1' -Source $PreviousSource -Output $oldOutput -Compiler $Compiler -AcceptanceFixture | Out-Null
& (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $Version -Source $Source -Output $newOutput -Compiler $Compiler -AcceptanceFixture | Out-Null
function Run-Quiet([string]$File,[string[]]$Extra,[string]$Log) {
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="'+$Log+'"'))+$Extra
    $process=Start-Process -FilePath $File -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer returned $($process.ExitCode); inspect $Log" }
}
Run-Quiet (Join-Path $oldOutput 'MojinDashuai-Setup-0.0.1-x64.exe') @('/TASKS=desktopicon',('/DIR="'+$installRoot+'"')) (Join-Path $testRoot 'install.log')
$installed=Join-Path $installRoot 'MojinDashuai.Launcher.exe'
if (!(Test-Path -LiteralPath $installed) -or (Get-ItemProperty $registryPath).DisplayVersion -ne '0.0.1') { throw 'Initial installation or uninstall registration missing' }
$linkName='魔金大帅 安装测试.lnk'
$desktopLink=Join-Path ([Environment]::GetFolderPath('Desktop')) $linkName
$menuLink=Join-Path ([Environment]::GetFolderPath('Programs')) $linkName
$shell=New-Object -ComObject WScript.Shell
if (!('InstallerPathCheck' -as [type])) {
    Add-Type @'
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerPathCheck {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint GetLongPathName(string path, StringBuilder buffer, uint length);
}
'@
}
function Long-Path([string]$Path) {
    $buffer=New-Object System.Text.StringBuilder 32768
    if ([InstallerPathCheck]::GetLongPathName($Path,$buffer,32768) -eq 0) { throw "Cannot resolve installed path: $Path" }
    $buffer.ToString()
}
foreach ($link in @($desktopLink,$menuLink)) {
    if (!(Test-Path -LiteralPath $link)) { throw "Installer shortcut missing: $link" }
    $actual=$shell.CreateShortcut($link).TargetPath
    if (!(Test-Path -LiteralPath $actual) -or !(Long-Path $actual).Equals((Long-Path $installed),[StringComparison]::OrdinalIgnoreCase)) { throw "Installer shortcut target mismatch: expected [$installed], got [$actual], link [$link]" }
}
$outside=Join-Path $testRoot '游戏文件\world.txt'
$inside=Join-Path $installRoot 'player-data\keep.txt'
foreach ($path in @($outside,$inside)) {
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    Set-Content -LiteralPath $path -Value 'player-owned-content' -Encoding utf8
}
Run-Quiet (Join-Path $newOutput "MojinDashuai-Setup-$Version-x64.exe") @() (Join-Path $testRoot 'upgrade.log')
if ((Get-ItemProperty $registryPath).DisplayVersion -ne $Version) { throw 'Upgrade version was not registered' }
if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Source 'MojinDashuai.Launcher.exe')).Hash) { throw 'Upgrade did not replace the application' }
if (!(Test-Path -LiteralPath (Join-Path $installRoot 'web/servers/dc2.png'))) { throw 'Server icons missing from installed application' }
$uninstaller=Join-Path $installRoot 'unins000.exe'
Run-Quiet $uninstaller @() (Join-Path $testRoot 'uninstall.log')
$deadline=[DateTime]::UtcNow.AddSeconds(30)
while ((Test-Path -LiteralPath $installed) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
if ((Test-Path $registryPath) -or (Test-Path -LiteralPath $installed)) { throw 'Uninstall left registered program files' }
foreach ($link in @($desktopLink,$menuLink)) { if (Test-Path -LiteralPath $link) { throw 'Uninstall left an owned shortcut' } }
foreach ($path in @($outside,$inside)) { if (!(Test-Path -LiteralPath $path) -or (Get-Content -LiteralPath $path -Raw).Trim() -ne 'player-owned-content') { throw 'Player-owned file was changed or removed' } }
$afterProcesses=@(Get-Process -Name 'MojinDashuai.Launcher','java','javaw' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
if ((@($beforeProcesses | Sort-Object) -join ',') -ne (@($afterProcesses | Sort-Object) -join ',')) { throw 'Installer started or terminated a launcher/game process' }
$result=[ordered]@{version=$Version;passed=$true;realInstallerExecuted=$true;separateTestAppId=$true;silent=$true;unicodeAndSpacePath=$true;shortcutsVerified=$true;perUserUninstallRegistration=$true;upgradeKeepsInstallDirectory=$true;upgradedFilesVerified=$true;uninstallVerified=$true;playerFilesPreservedInsideAndOutsideInstall=$true;gameOrLauncherStarted=$false;cleanWindows=$false}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'report.json') -Encoding utf8
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repository 'packs/installer-acceptance.json') -Encoding utf8
$result | ConvertTo-Json
