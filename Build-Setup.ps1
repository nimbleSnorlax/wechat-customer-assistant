param(
    [string] $Repository,

    [string] $Output,
    [string] $Version,
    [string] $ReaderSource,
    [switch] $Offline
)

$ErrorActionPreference = 'Stop'
if ([bool]$Repository -eq $Offline.IsPresent) {
    throw '请指定 GitHub 发布仓库，或使用 -Offline。'
}
$dotnet = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
& $dotnet build (Join-Path $PSScriptRoot 'sdk-bridge\WeChatBridge.csproj') -c Release -o (Join-Path $PSScriptRoot '.run\sdk')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$arguments = @((Join-Path $PSScriptRoot 'installer\build_setup.py'))
if ($Offline) { $arguments += '--offline' }
else { $arguments += $Repository }
if ($Output) { $arguments += @('--output', $Output) }
if ($Version) { $arguments += @('--version', $Version) }
if ($ReaderSource) { $arguments += @('--reader-source', $ReaderSource) }
& python @arguments
exit $LASTEXITCODE
