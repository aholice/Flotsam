param(
  [Parameter(Mandatory=$true)][string]$GameDir,
  [Parameter(Mandatory=$true)][string]$ProjectDir,
  [Parameter(Mandatory=$true)][string]$Csc
)

$ErrorActionPreference = 'Stop'

$managed = Join-Path $GameDir 'Flotsam_Data\Managed'
$core    = Join-Path $GameDir 'BepInEx\core'
$src     = Join-Path $ProjectDir 'src'
$outDir  = Join-Path $ProjectDir 'dist\BepInEx\plugins\FlotsamEnergyRechargeMod'
$outDll  = Join-Path $outDir 'FlotsamEnergyRechargeMod.dll'

New-Item -ItemType Directory -Force $outDir | Out-Null

$refs = @(
  (Join-Path $managed 'mscorlib.dll'),
  (Join-Path $managed 'System.dll'),
  (Join-Path $managed 'System.Core.dll'),
  (Join-Path $managed 'netstandard.dll'),
  (Join-Path $managed 'UnityEngine.dll'),
  (Join-Path $managed 'UnityEngine.CoreModule.dll'),
  (Join-Path $managed 'UnityEngine.IMGUIModule.dll'),
  (Join-Path $managed 'UnityEngine.TextRenderingModule.dll'),
  (Join-Path $managed 'Assembly-CSharp.dll'),
  (Join-Path $core 'BepInEx.dll'),
  (Join-Path $core '0Harmony.dll')
)

foreach ($r in $refs) {
  if (-not (Test-Path $r)) { throw "Missing reference: $r" }
}

$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
  '/nologo',
  '/target:library',
  '/langversion:9',
  '/optimize+',
  '/nostdlib+',
  '/deterministic+',
  ('/out:' + $outDll)
)
foreach ($r in $refs) { $cscArgs += ('/reference:' + $r) }
$cscArgs += $sources

& $Csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

"BUILD OK -> $outDll ($([math]::Round((Get-Item $outDll).Length / 1kb, 1)) KB)"
