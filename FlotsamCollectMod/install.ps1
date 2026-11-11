param(
  [Parameter(Mandatory=$true)][string]$GameDir
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'dist\BepInEx'
if (-not (Test-Path $src)) { throw "Build output not found: $src (run build.ps1 first)" }

$dst = Join-Path $GameDir 'BepInEx'
New-Item -ItemType Directory -Force (Join-Path $dst 'plugins') | Out-Null
Copy-Item (Join-Path $src 'plugins\FlotsamCollectMod') (Join-Path $dst 'plugins') -Recurse -Force

$dll = Join-Path $dst 'plugins\FlotsamCollectMod\FlotsamCollectMod.dll'
if (-not (Test-Path $dll)) { throw "Install failed: $dll missing" }

"Installed -> $dll"
"Start the game, then press F7 in a save to open the item panel."
