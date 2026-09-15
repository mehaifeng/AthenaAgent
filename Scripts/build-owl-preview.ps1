param([string]$CatalogPath = 'artifacts/owl-qa/owl-clips.json')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$catalog = Get-Content (Join-Path $repoRoot $CatalogPath) -Raw
$template = Get-Content (Join-Path $PSScriptRoot 'owl-animation-preview.template.html') -Raw
$destination = Join-Path $repoRoot 'Docs/OwlAnimationPreview.html'
[IO.File]::WriteAllText($destination, $template.Replace('__CATALOG__', $catalog), [Text.UTF8Encoding]::new($false))
Write-Output $destination
