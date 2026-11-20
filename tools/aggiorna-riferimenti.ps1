# Copia gli screenshot dell'ultima build (ramo "screenshots") nelle immagini di riferimento dei test.
# Da usare quando un cambiamento dell'interfaccia è voluto; poi rivedere e committare le immagini.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo 'tests/PcOrg.App.Tests/Riferimenti'
git -C $repo fetch origin screenshots
$archive = Join-Path ([IO.Path]::GetTempPath()) 'screenshots.tar'
git -C $repo archive --format=tar -o $archive origin/screenshots
tar -xf $archive -C $dest
Remove-Item $archive
Write-Host "Riferimenti aggiornati in $dest: controlla le immagini e committale."
