param([string]$BuildId='1.0.0-startup-debug.1',[string]$Dotnet=(Join-Path $PSScriptRoot '..\..\.tools\dotnet10\dotnet.exe'),[switch]$DisableCet)
$ErrorActionPreference='Stop'
if($BuildId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]+$') { throw 'Invalid build identifier' }
$repository=Split-Path $PSScriptRoot -Parent
$target=Join-Path $repository ('artifacts\diagnostics\'+$BuildId)
if(Test-Path -LiteralPath $target) { throw 'Diagnostic build directory already exists; use another BuildId' }
$app=Join-Path $target 'app'
# CetCompat is not an input timestamp of the SDK apphost target. A fresh native
# host output is required so a previously cached CET-enabled exe is not reused.
$project=Join-Path $repository 'src\Launcher.Desktop\Launcher.Desktop.csproj'
$cachedHost=(& $Dotnet msbuild $project -t:_GetAppHostPaths -getProperty:AppHostIntermediatePath -p:RuntimeIdentifier=win-x64 -p:Configuration=Release | Out-String).Trim()
if($LASTEXITCODE -ne 0) { throw 'Cannot resolve the native apphost build output' }
$objRoot=[IO.Path]::GetFullPath((Join-Path $repository 'src\Launcher.Desktop\obj'))
$cachedHost=[IO.Path]::GetFullPath($cachedHost)
if(!$cachedHost.StartsWith($objRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($cachedHost) -ne 'apphost.exe') { throw 'Unexpected apphost cache path' }
if(Test-Path -LiteralPath $cachedHost -PathType Leaf) { Remove-Item -LiteralPath $cachedHost }
$extra=@()
if($DisableCet) { $extra+= '-p:CETCompat=false' }
& $Dotnet publish (Join-Path $repository 'src\Launcher.Desktop\Launcher.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $app @extra
$publishExit=$LASTEXITCODE
# Do not leave a special compatibility host cached for a later ordinary release.
if(Test-Path -LiteralPath $cachedHost -PathType Leaf) { Remove-Item -LiteralPath $cachedHost }
if($publishExit -ne 0) { throw 'Diagnostic launcher build failed' }
$cetDisabled=((& $Dotnet msbuild $project -getProperty:CETCompat @extra | Out-String).Trim() -eq 'false')
if($LASTEXITCODE -ne 0) { throw 'Cannot resolve the effective CET compatibility property' }
& python (Join-Path $PSScriptRoot 'check-apphost-cet.py') (Join-Path $app 'MojinDashuai.Launcher.exe') --expected $(if($cetDisabled){'disabled'}else{'enabled'})
if($LASTEXITCODE -ne 0) { throw 'Diagnostic native host CET flag does not match the requested build' }
$scripts=Join-Path $target 'diagnostics'
[IO.Directory]::CreateDirectory($scripts) | Out-Null
# Windows PowerShell 5.1 requires a BOM for the Chinese user messages.
$bom=New-Object Text.UTF8Encoding($true)
$source=Join-Path $PSScriptRoot 'startup-diagnostics'
[IO.File]::WriteAllText((Join-Path $scripts 'run.ps1'),[IO.File]::ReadAllText((Join-Path $source 'run.ps1')),$bom)
[IO.File]::WriteAllText((Join-Path $target '使用说明.txt'),[IO.File]::ReadAllText((Join-Path $source 'README.txt')),$bom)
$commands=@{'启动诊断.cmd'='';'兼容模式诊断.cmd'=' -Mode compatibility';'收集日志.cmd'=' -CollectOnly'}
foreach($name in $commands.Keys) {
    $cmd='@echo off'+"`r`n"+'cd /d "%~dp0"'+"`r`n"+'powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnostics\run.ps1"'+$commands[$name]+"`r`n"+'if errorlevel 1 pause'+"`r`n"
    [IO.File]::WriteAllText((Join-Path $target $name),$cmd,[Text.Encoding]::ASCII)
}
$record=@{buildId=$BuildId;purpose='Remote startup diagnosis only';baseVersion='1.0.0';selfContained=$true;automaticUpdatePublished=$false;cetCompatibilityOptOut=$cetDisabled;builtAt=[DateTimeOffset]::UtcNow.ToString('o')}
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'diagnostic-build.json') -Encoding UTF8
$archive=Join-Path (Split-Path $target -Parent) ('MojinDashuai-'+$BuildId+'-x64.zip')
Compress-Archive -LiteralPath $target -DestinationPath $archive
$record.archive=$archive;$record.bytes=(Get-Item -LiteralPath $archive).Length;$record.sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$record | ConvertTo-Json | Set-Content -LiteralPath ($archive+'.json') -Encoding UTF8
$record | ConvertTo-Json
