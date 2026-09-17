[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath)) { throw 'Install Visual Studio 2026 with the Visual Studio extension development workload.' }
$vsPath = & $vswherePath -latest -products '*' -version '[18.5,)' -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsPath) { throw 'Visual Studio 2026 version 18.5 or later with MSBuild is required.' }
$msbuildPath = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
$artifactPath = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
$installerDirectory = Join-Path $projectRoot 'installer'
$installerPath = Join-Path $installerDirectory 'SourceTrace.vsix'
New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null

& $msbuildPath (Join-Path $projectRoot 'SourceTrace.slnx') /restore "/p:Configuration=$Configuration" "/p:TargetVsixContainer=$installerPath" /p:DeployExtension=false /p:EnableIntegrationDiagnostics=false /p:TreatWarningsAsErrors=true /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

& dotnet (Join-Path $projectRoot "tests\SourceTrace.Tests\bin\$Configuration\net10.0\SourceTrace.Tests.dll") | Tee-Object -FilePath (Join-Path $artifactPath 'test-results.txt')
if ($LASTEXITCODE -ne 0) { throw 'SourceTrace tests failed.' }

& (Join-Path $projectRoot "tests\SourceTrace.EditorTests\bin\$Configuration\net472\SourceTrace.EditorTests.exe") | Tee-Object -FilePath (Join-Path $artifactPath 'editor-test-results.txt')
if ($LASTEXITCODE -ne 0) { throw 'SourceTrace editor tests failed.' }

Get-FileHash -LiteralPath $installerPath -Algorithm SHA256 | Format-List
Write-Host "Installer ready: $installerPath"
