param([string[]]$Clips = @())
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Drawing.Common
$compilerSource = (Get-Content (Join-Path $PSScriptRoot 'OwlPaletteTransfer.cs.txt') -Raw) + [Environment]::NewLine +
    (Get-Content (Join-Path $PSScriptRoot 'OwlSpriteImport.cs.txt') -Raw)
Add-Type -TypeDefinition $compilerSource -ReferencedAssemblies @(
    [Drawing.Bitmap].Assembly.Location,
    [Drawing.Rectangle].Assembly.Location,
    [System.Linq.Enumerable].Assembly.Location,
    [System.Collections.Generic.List[int]].Assembly.Location,
    'System.Runtime', 'System.Runtime.InteropServices', 'System.Collections',
    (Join-Path $PSHOME 'System.Private.Windows.GdiPlus.dll'),
    (Join-Path $PSHOME 'System.Private.Windows.Core.dll')
)
$palette = [OwlPaletteTransfer]::Build(
    (Join-Path $repoRoot 'Artwork/SubAgents/idle.png'),
    (Join-Path $repoRoot 'Artwork/SubAgents/palette-master-v1.png'))
$settings = Get-Content (Join-Path $repoRoot 'Artwork/SubAgents/registration.json') -Raw | ConvertFrom-Json
foreach ($clip in $settings) {
    if ($Clips.Count -gt 0 -and $clip.name -notin $Clips) { continue }
    $sourcePath = Join-Path $repoRoot "Artwork/SubAgents/$($clip.name).png"
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Missing source: $sourcePath" }
    $destination = Join-Path $repoRoot "Assets/SubAgents/V2/$($clip.name)"
    $report = [OwlSpriteImport]::Import($sourcePath, $destination, $clip.count, $clip.scale,
        [double[]]$clip.anchorX, [double[]]$clip.anchorY, $clip.targetX, $clip.targetY, $palette)
    [IO.File]::WriteAllText((Join-Path $repoRoot "Artwork/SubAgents/$($clip.name)-registration.csv"), $report)
    Write-Output "$($clip.name): $($clip.count) isolated frames exported"
}
