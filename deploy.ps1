# Builds the mod and copies it into the game's UMM Mods folder for in-game testing.
#   .\deploy.ps1
#   .\deploy.ps1 -GameDir "D:\Steam\steamapps\common\Derail Valley"
param (
	[string]$GameDir = "E:\SteamLibrary\steamapps\common\Derail Valley"
)

$ErrorActionPreference = "Stop"
Set-Location "$PSScriptRoot"

if (Get-Process -Name DerailValley -ErrorAction SilentlyContinue) {
	throw "Derail Valley is running; quit it first so the DLL isn't locked."
}

# Release build also runs package.ps1, which stages dist/tmp/<modId>
dotnet build DerailValleyWebSocket -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$modId = (Get-Content -Raw "info.json" | ConvertFrom-Json).Id
$source = "$PSScriptRoot/dist/tmp/$modId"
$target = "$GameDir/Mods/$modId"

New-Item -ItemType Directory -Force "$target" | Out-Null
Copy-Item -Force -Path "$source/*" -Destination "$target"
Write-Host "Deployed to $target"
