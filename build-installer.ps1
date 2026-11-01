[CmdletBinding()]
param(
	[string] $BunVersion,
	[switch] $SkipAppx
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$installer = Join-Path $root 'installer'
$assets = Join-Path $installer 'assets'
$explorer = Join-Path $installer 'explorer-command'
$out = Join-Path $root 'artifacts\installer'
$app = Join-Path $out 'app'
$obj = Join-Path $out 'obj'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props' }

$clsid = '{1D2D8C77-3100-4E5C-ADF8-E35830DCDFBE}'
$packageName = 'ForgeDev.ForgeDev'
$publisher = 'CN=ForgeDev'
$displayName = 'Forge Dev'
$dllName = 'forge_dev_explorer_command.dll'
$appxName = 'forge_dev_shell.appx'
$pfx = Join-Path $explorer 'forge-dev-shell.pfx'
$cer = Join-Path $explorer 'forge-dev-shell.cer'
$pfxPassword = 'forgedev'

function Step([string] $text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

if (-not $BunVersion) {
	Step 'Resolving latest bun'
	$release = Invoke-RestMethod 'https://api.github.com/repos/oven-sh/bun/releases/latest' -Headers @{ 'User-Agent' = 'Forge' }
	$BunVersion = $release.tag_name -replace '^bun-v', ''
}
Write-Host "bun $BunVersion"

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $app, $obj | Out-Null

Step "Publishing Forge $version (self-contained, ReadyToRun)"
dotnet publish (Join-Path $root 'Forge.App\Forge.App.csproj') -c Release -r win-x64 --self-contained true --nologo `
	-p:PublishReadyToRun=true -p:DebugType=none -p:SatelliteResourceLanguages=en `
	-p:BunVersion=$BunVersion -p:ForgeDev=true -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$hasAppx = $false
$thumbprint = ''

if (-not $SkipAppx) {
	Step 'Building Windows 11 context menu package'

	if (-not (Test-Path $pfx)) {
		Write-Host 'Creating self-signed shell certificate (commit installer\explorer-command\forge-dev-shell.*)'
		$cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName "$displayName shell" `
			-CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(20) `
			-TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
		Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) | Out-Null
		Export-Certificate -Cert $cert -FilePath $cer | Out-Null
		Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
	}

	$thumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)).Thumbprint

	$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
	$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
	if (-not $vs) { throw 'No VC++ toolchain found' }
	$env:PATH = "$(Split-Path $vswhere);$env:PATH"

	$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64" -Directory | Sort-Object Name -Descending |
		Where-Object { Test-Path (Join-Path $_.FullName 'makeappx.exe') } | Select-Object -First 1
	if (-not $sdkBin) { throw 'Windows SDK (makeappx/signtool) not found' }

	$config = Join-Path $obj 'explorer-command-config.h'
	@(
		"#define EXPLORER_COMMAND_CLSID L`"$clsid`""
		"#define EXPLORER_COMMAND_TITLE L`"Open in $displayName`""
		"#define EXPLORER_COMMAND_EXE L`"Forge`""
	) | Set-Content $config -Encoding ASCII

	$dll = Join-Path $obj $dllName
	cmd /c "call `"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl /nologo /O1 /GL /LD /EHsc /std:c++17 /DUNICODE /D_UNICODE /FI`"$config`" `"$explorer\explorer-command.cpp`" /Fo`"$obj\\`" /Fe`"$dll`" /link /LTCG /OPT:REF /OPT:ICF /DEF:`"$explorer\explorer-command.def`" shlwapi.lib shell32.lib ole32.lib"
	if (-not (Test-Path $dll)) { throw 'Explorer command compilation failed' }

	$stage = Join-Path $obj 'appx'
	New-Item -ItemType Directory -Path "$stage\appx" | Out-Null
	(Get-Content (Join-Path $explorer 'AppxManifest.xml') -Raw).
		Replace('@@PackageName@@', $packageName).
		Replace('@@Publisher@@', $publisher).
		Replace('@@Version@@', "$version.0").
		Replace('@@DisplayName@@', $displayName).
		Replace('@@Executable@@', 'Forge.exe').
		Replace('@@Clsid@@', $clsid.Trim('{', '}')).
		Replace('@@Dll@@', $dllName) | Set-Content (Join-Path $stage 'AppxManifest.xml') -Encoding UTF8
	Copy-Item "$assets\forge-dev-150.png", "$assets\forge-dev-44.png" "$stage\appx"

	$appx = Join-Path $obj $appxName
	& "$($sdkBin.FullName)\makeappx.exe" pack /o /nv /d $stage /p $appx | Out-Null
	if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }

	& "$($sdkBin.FullName)\signtool.exe" sign /q /fd SHA256 /f $pfx /p $pfxPassword $appx
	if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

	New-Item -ItemType Directory -Path "$app\appx" | Out-Null
	Copy-Item $appx, $dll, $cer, "$assets\forge-dev-150.png", "$assets\forge-dev-44.png" "$app\appx"
	$hasAppx = $true
}

Step 'Compiling installer'
$iscc = @(
	"${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
	"$env:ProgramFiles\Inno Setup 6\ISCC.exe",
	"$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
	Write-Host 'Inno Setup not found, installing via winget'
	winget install --id JRSoftware.InnoSetup -e --silent --accept-source-agreements --accept-package-agreements --scope user | Out-Null
	$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
	if (-not (Test-Path $iscc)) { throw 'Inno Setup install failed' }
}

$defines = @("/DVersion=$version", "/DSourceDir=$app", "/DOutputDir=$out", "/DAssetsDir=$assets")
if ($hasAppx) { $defines += @('/DHasAppx', "/DCertThumbprint=$thumbprint") }

& $iscc /Q @defines (Join-Path $installer 'forge.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$setup = Get-ChildItem $out -Filter 'ForgeDevSetup-*.exe' | Select-Object -First 1
Write-Host "`n$($setup.FullName) ($([math]::Round($setup.Length / 1MB, 1)) MB)" -ForegroundColor Green
