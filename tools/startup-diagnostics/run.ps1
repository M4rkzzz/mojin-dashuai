param(
    [ValidateSet('normal','compatibility')][string]$Mode='normal',
    [ValidateRange(3,120)][int]$ObserveSeconds=40,
    [switch]$NoPause,
    [switch]$CollectOnly
)
$ErrorActionPreference='Stop'
$packageRoot=Split-Path $PSScriptRoot -Parent
$historyRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Boshan\Launcher\startup-diagnostics'
$utf8=New-Object System.Text.UTF8Encoding($false)

function Write-Utf8([string]$Path,[string]$Text) {
    [IO.File]::WriteAllText($Path,$Text,$utf8)
}
function Hide-PrivateText([string]$Text) {
    if ($null -eq $Text) { return '' }
    $paths=@([Environment]::GetFolderPath('UserProfile'),$packageRoot) | Where-Object { $_ } | Sort-Object Length -Descending
    foreach($item in $paths) {
        $Text=$Text -replace [regex]::Escape($item.Replace('\','\\')),'[LOCAL_PATH]'
        $Text=$Text -replace [regex]::Escape($item),'[LOCAL_PATH]'
    }
    $Text=$Text -replace '(?i)C:\\Users\\[^\\\s"<>]+','[USER]'
    $Text=$Text -replace '(?im)^.*(?:authorization["\s]*[:=]|bearer\s+|access.?token["\s]*[:=]|refresh.?token["\s]*[:=]|password["\s]*[:=]|client.?secret["\s]*[:=]|cookie["\s]*[:=]).*$','[REDACTED]'
    return $Text
}
function Record([string]$Event,$Details) {
    $line=@{at=[DateTimeOffset]::UtcNow.ToString('o');event=$Event;details=$Details} | ConvertTo-Json -Depth 6 -Compress
    [IO.File]::AppendAllText((Join-Path $runRoot 'collector.jsonl'),(Hide-PrivateText $line)+[Environment]::NewLine,$utf8)
}
function Export-Report([string]$Directory) {
    $exportRoot=Join-Path $Directory 'export'
    [IO.Directory]::CreateDirectory($exportRoot) | Out-Null
    # Only these diagnostics belong in a report. Never recurse into settings, accounts or browser profiles.
    $allowed=@('collector.jsonl','startup.jsonl','dotnet-host.txt','stdout.txt','stderr.txt','system.json','events.json')
    foreach($name in $allowed) {
        $source=Join-Path $Directory $name
        if(!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $stream=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try {
            $limit=4MB
            if($stream.Length -gt $limit) { [void]$stream.Seek(-$limit,[IO.SeekOrigin]::End) }
            $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8,$true)
            try { $text=$reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
        Write-Utf8 (Join-Path $exportRoot $name) (Hide-PrivateText $text)
    }
    Write-Utf8 (Join-Path $exportRoot '说明.txt') '这是魔金大帅启动诊断报告，仅收集启动阶段、运行环境与异常信息，不包含账号文件、浏览器资料、游戏存档或内存转储。请将此 ZIP 发给管理员。'
    $desktop=[Environment]::GetFolderPath('Desktop')
    if(!$desktop -or !(Test-Path -LiteralPath $desktop -PathType Container)) { $desktop=$packageRoot }
    $name='Mojin-Startup-Diagnostics-'+(Split-Path $Directory -Leaf)+'.zip'
    $archive=Join-Path $desktop $name
    try { Compress-Archive -LiteralPath $exportRoot -DestinationPath $archive -Force }
    catch { $archive=Join-Path $Directory $name; Compress-Archive -LiteralPath $exportRoot -DestinationPath $archive -Force }
    Write-Host ''
    Write-Host '诊断报告已生成，请把下面的 ZIP 文件发给管理员：' -ForegroundColor Green
    Write-Host $archive -ForegroundColor Yellow
    Write-Utf8 (Join-Path $Directory 'report-path.txt') $archive
    return $archive
}

try {
    [IO.Directory]::CreateDirectory($historyRoot) | Out-Null
    if($CollectOnly) {
        $latest=Get-ChildItem -LiteralPath $historyRoot -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}-[a-f0-9]{8}$' } | Sort-Object Name -Descending | Select-Object -First 1
        if(!$latest) { throw '请先运行“启动诊断.cmd”，再收集日志。' }
        $runRoot=$latest.FullName
        [void](Export-Report $runRoot)
    } else {
        $runId=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
        $runRoot=Join-Path $historyRoot $runId
        [IO.Directory]::CreateDirectory($runRoot) | Out-Null
        Write-Host '魔金大帅启动诊断' -ForegroundColor Green
        Write-Host ('模式：'+$(if($Mode -eq 'normal'){'普通诊断'}else{'兼容诊断（跳过更新等待、软件渲染、独立显示缓存）'}))
        Write-Host ('即使主程序没有出现，也请等候约 '+$ObserveSeconds+' 秒，随后会生成日志 ZIP。')
        Write-Host '不需要登录、下载游戏或安装 Java。请勿关闭这个诊断窗口。'
        $exe=Join-Path $packageRoot 'app\MojinDashuai.Launcher.exe'
        $versions=@()
        foreach($base in @('HKCU:\Software\Microsoft\EdgeUpdate\Clients','HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients','HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients')) {
            if(Test-Path $base) {
                foreach($key in Get-ChildItem $base -ErrorAction SilentlyContinue) {
                    $v=Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
                    if($v.name -match 'WebView') { $versions+=@{name=$v.name;version=$v.pv;scope=$base.Split(':')[0]} }
                }
            }
        }
        $shortcuts=@()
        try {
            $shell=New-Object -ComObject WScript.Shell
            foreach($folder in @([Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('Programs'))) {
                if(!$folder -or !(Test-Path -LiteralPath $folder)) { continue }
                foreach($link in Get-ChildItem -LiteralPath $folder -Filter '*魔金大帅*.lnk' -File -ErrorAction SilentlyContinue) {
                    $shortcut=$shell.CreateShortcut($link.FullName)
                    $shortcuts+=@{name=$link.Name;target=(Hide-PrivateText $shortcut.TargetPath);targetExists=(Test-Path -LiteralPath $shortcut.TargetPath -PathType Leaf);workingDirectoryExists=(Test-Path -LiteralPath $shortcut.WorkingDirectory -PathType Container)}
                }
            }
        } catch { Record 'shortcut-inspection-failed' @{type=$_.Exception.GetType().FullName;hresult=$_.Exception.HResult} }
        $screen=@()
        try { Add-Type -AssemblyName System.Windows.Forms; $screen=@([Windows.Forms.Screen]::AllScreens | ForEach-Object {@{x=$_.Bounds.X;y=$_.Bounds.Y;width=$_.Bounds.Width;height=$_.Bounds.Height;workingWidth=$_.WorkingArea.Width;workingHeight=$_.WorkingArea.Height;primary=$_.Primary}}) } catch {}
        $osKey=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
        $system=@{mode=$Mode;os=[Environment]::OSVersion.VersionString;windowsBuild=$osKey.CurrentBuild;windowsRevision=$osKey.UBR;displayVersion=$osKey.DisplayVersion;os64Bit=[Environment]::Is64BitOperatingSystem;process64Bit=[Environment]::Is64BitProcess;architecture=$env:PROCESSOR_ARCHITECTURE;processorCount=[Environment]::ProcessorCount;powerShell=$PSVersionTable.PSVersion.ToString();screens=$screen;webViewVersions=$versions;shortcuts=$shortcuts;alreadyRunning=@(Get-Process -Name 'MojinDashuai.Launcher' -ErrorAction SilentlyContinue | ForEach-Object {@{id=$_.Id;windowVisible=($_.MainWindowHandle -ne 0)}})}
        Write-Utf8 (Join-Path $runRoot 'system.json') (Hide-PrivateText ($system|ConvertTo-Json -Depth 7))
        Record 'collector-start' @{mode=$Mode;executableExists=(Test-Path -LiteralPath $exe -PathType Leaf)}
        if(!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw '诊断程序不完整，请先将整个 ZIP 解压后再运行。' }
        Record 'executable' @{sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;version=(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion}
        $environmentNames=@('MOJIN_STARTUP_DIAGNOSTICS_DIR','MOJIN_STARTUP_COMPATIBILITY','DOTNET_HOST_TRACE','DOTNET_HOST_TRACEFILE','DOTNET_HOST_TRACE_VERBOSITY','COREHOST_TRACE','COREHOST_TRACEFILE','COREHOST_TRACE_VERBOSITY')
        $oldEnvironment=@{}
        foreach($name in $environmentNames) { $oldEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
        $startedAt=Get-Date
        try {
            $env:MOJIN_STARTUP_DIAGNOSTICS_DIR=$runRoot
            $env:MOJIN_STARTUP_COMPATIBILITY=$(if($Mode -eq 'compatibility'){'1'}else{'0'})
            $env:DOTNET_HOST_TRACE='1';$env:COREHOST_TRACE='1'
            $env:DOTNET_HOST_TRACEFILE=Join-Path $runRoot 'dotnet-host.txt';$env:COREHOST_TRACEFILE=$env:DOTNET_HOST_TRACEFILE
            $env:DOTNET_HOST_TRACE_VERBOSITY='3';$env:COREHOST_TRACE_VERBOSITY='3'
            # This is the visible launcher the player requested, not a background service.
            $launched=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -RedirectStandardOutput (Join-Path $runRoot 'stdout.txt') -RedirectStandardError (Join-Path $runRoot 'stderr.txt') -WindowStyle Normal -PassThru
        } finally { foreach($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$oldEnvironment[$name],'Process') } }
        Record 'process-started' @{id=$launched.Id;startedAt=$launched.StartTime.ToUniversalTime().ToString('o')}
        for($elapsed=0;$elapsed -lt $ObserveSeconds;$elapsed+=2) {
            Start-Sleep -Seconds 2
            $launched.Refresh()
            if($launched.HasExited) { Record 'process-exited' @{code=$launched.ExitCode;elapsedSeconds=$elapsed+2}; Write-Host ('主程序已退出，退出码：'+$launched.ExitCode); break }
            Record 'process-sample' @{elapsedSeconds=$elapsed+2;windowHandlePresent=($launched.MainWindowHandle -ne 0);responding=$launched.Responding;workingSetBytes=$launched.WorkingSet64;cpuMilliseconds=$launched.TotalProcessorTime.TotalMilliseconds}
            if(($elapsed+2)%10 -eq 0) { Write-Host ('已采集 '+($elapsed+2)+' 秒…') }
        }
        # Startup crash messages are limited to this process and this observation window.
        $events=@()
        try {
            foreach($event in Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=$startedAt;Id=1000,1001,1026} -MaxEvents 30 -ErrorAction Stop) {
                if($event.Message -notmatch 'MojinDashuai\.Launcher') { continue }
                $message=$event.Message
                if($message.Length -gt 12000) {$message=$message.Substring(0,12000)}
                $events+=@{id=$event.Id;provider=$event.ProviderName;at=$event.TimeCreated.ToUniversalTime().ToString('o');message=(Hide-PrivateText $message)}
            }
        } catch { Record 'event-log-read' @{result='no-matching-events-or-unavailable';type=$_.Exception.GetType().FullName} }
        Write-Utf8 (Join-Path $runRoot 'events.json') (ConvertTo-Json -InputObject @($events) -Depth 5)
        Record 'observation-complete' @{processId=$launched.Id;stillRunning=(-not $launched.HasExited);managedLogExists=(Test-Path -LiteralPath (Join-Path $runRoot 'startup.jsonl'))}
        [void](Export-Report $runRoot)
        Write-Host '如果普通诊断仍没有界面，可关闭本次主程序后再运行“兼容模式诊断.cmd”。'
        Write-Host '若稍后才出现错误，可双击“收集日志.cmd”重新生成最新报告。'
    }
} catch {
    Write-Host ('诊断运行失败：'+$_.Exception.Message) -ForegroundColor Red
    if($runRoot -and (Test-Path -LiteralPath $runRoot)) {
        Record 'collector-failed' @{type=$_.Exception.GetType().FullName;hresult=$_.Exception.HResult;message=(Hide-PrivateText $_.Exception.Message)}
        try { [void](Export-Report $runRoot) } catch { Write-Host ('日志位置：'+$runRoot) }
    }
}
if(!$NoPause) { [void](Read-Host '按回车关闭此诊断窗口（不会关闭主程序）') }
