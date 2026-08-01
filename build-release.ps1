param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "src\VictusControl.App\VictusControl.App.csproj"
$publishDir = Join-Path $PSScriptRoot "publish\setup"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $project `
    -c $Configuration `
    -r $RuntimeIdentifier `
    -p:PublishProfile=ReleaseSetup

Get-ChildItem $publishDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$setupExe = Join-Path $publishDir "VictusControl.Setup.exe"
Write-Host "Setup artifact: $setupExe"