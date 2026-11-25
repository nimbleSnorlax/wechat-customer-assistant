param(
    [Parameter(Mandatory)]
    [string] $Version
)
python (Join-Path $PSScriptRoot 'release_update.py') --build $Version
exit $LASTEXITCODE
