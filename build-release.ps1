param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$appProject = Join-Path $PSScriptRoot "src\VictusControl.App\VictusControl.App.csproj"
$setupProject = Join-Path $PSScriptRoot "src\VictusControl.Setup\VictusControl.Setup.csproj"
$appPublishDir = Join-Path $PSScriptRoot "publish\app"
$setupPublishDir = Join-Path $PSScriptRoot "publish\setup"
$payloadZip = Join-Path $PSScriptRoot "src\VictusControl.Setup\obj\VictusControl.App.zip"

foreach ($path in @($appPublishDir, $setupPublishDir, $payloadZip)) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

dotnet publish $appProject `
    -c $Configuration `
    -r $RuntimeIdentifier `
    -o $appPublishDir

Compress-Archive -Path (Join-Path $appPublishDir '*') -DestinationPath $payloadZip -Force

dotnet publish $setupProject `
    -c $Configuration `
    -r $RuntimeIdentifier `
    -p:InstallerPayloadZip=$payloadZip `
    -o $setupPublishDir

Get-ChildItem $setupPublishDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $appPublishDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

if (Test-Path $appPublishDir) {
    Remove-Item $appPublishDir -Recurse -Force
}

if (Test-Path $payloadZip) {
    Remove-Item $payloadZip -Force
}

$setupExe = Join-Path $setupPublishDir "VictusControl.Setup.exe"
Write-Host "Setup artifact: $setupExe"