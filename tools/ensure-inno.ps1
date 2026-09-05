param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\innosetup-6.7.3'))
$ErrorActionPreference='Stop'
$compiler=Join-Path $Destination 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { return (Resolve-Path -LiteralPath $compiler).Path }
$parent=Split-Path $Destination -Parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$download=Join-Path $parent 'innosetup-6.7.3.exe'
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732') { throw 'Inno Setup download hash mismatch' }
$signature=Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Inno Setup publisher verification failed' }
$arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/NOICONS',('/DIR="'+[IO.Path]::GetFullPath($Destination)+'"'))
$process=Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup installation failed' }
(Resolve-Path -LiteralPath $compiler).Path
