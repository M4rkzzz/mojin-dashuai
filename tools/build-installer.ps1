param(
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Compiler,
    [switch]$AcceptanceFixture
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(\d+\.\d+\.\d+)(?:-[a-z0-9.]+)?$') { throw 'Invalid launcher version' }
$numericVersion = $Matches[1] + '.0'
$repository = Split-Path $PSScriptRoot -Parent
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
if (!(Test-Path -LiteralPath (Join-Path $sourcePath 'MojinDashuai.Launcher.exe')) -or !(Test-Path -LiteralPath (Join-Path $sourcePath 'web/index.html'))) { throw 'Publish the complete launcher before building its installer' }
if ([version]($Version.Split('-')[0]) -ge [version]'1.0.1') {
    & python (Join-Path $PSScriptRoot 'check-apphost-cet.py') (Join-Path $sourcePath 'MojinDashuai.Launcher.exe') --expected disabled
    if ($LASTEXITCODE -ne 0) { throw 'Launcher apphost must include the old-Windows CET compatibility fix' }
}
$outputPath = [IO.Path]::GetFullPath($Output)
if (!$Compiler) {
    $candidates = @(
        (Join-Path $repository '..\.tools\innosetup-6.7.3\ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$Compiler -or !(Test-Path -LiteralPath $Compiler)) { throw 'Inno Setup 6.7.3 or later is required; pass -Compiler with ISCC.exe' }
$arguments = @('/Qp', "/DAppVersion=$Version", "/DNumericVersion=$numericVersion", "/DSourceDir=$sourcePath", "/DOutputPath=$outputPath")
if ($AcceptanceFixture) {
    $arguments += @('/DAppIdentity=MojinDashuai.Installer.Acceptance', '/DInstallFolder=MojinDashuai-Installer-Acceptance', '/DShortcutName=魔金大帅 安装测试')
}
$arguments += Join-Path $repository 'installer\MojinDashuai.iss'
& $Compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$setup = Join-Path $outputPath "MojinDashuai-Setup-$Version-x64.exe"
$result = [ordered]@{version=$Version;fileName=[IO.Path]::GetFileName($setup);bytes=(Get-Item -LiteralPath $setup).Length;sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant();format='inno-setup';perUser=$true;acceptanceFixture=[bool]$AcceptanceFixture}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'installer.json') -Encoding utf8
$result | ConvertTo-Json
