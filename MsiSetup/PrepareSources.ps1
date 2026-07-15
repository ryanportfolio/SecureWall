param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildOutput = Join-Path $repoRoot "TinyWall\bin\$Configuration"
$installerOutput = Join-Path $PSScriptRoot 'Sources\ProgramFiles\PromptWall'

$requiredFiles = @(
    'PromptWall.exe',
    'PromptWall.exe.config',
    'Microsoft.Bcl.AsyncInterfaces.dll',
    'System.Buffers.dll',
    'System.IO.Pipelines.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encodings.Web.dll',
    'System.Text.Json.dll',
    'System.Threading.Tasks.Extensions.dll'
)

foreach ($file in $requiredFiles) {
    $source = Join-Path $buildOutput $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing build output: $source"
    }
}

New-Item -ItemType Directory -Force -Path $installerOutput | Out-Null
foreach ($file in $requiredFiles) {
    Copy-Item -LiteralPath (Join-Path $buildOutput $file) -Destination (Join-Path $installerOutput $file) -Force
}

$cultures = @('bg', 'cs', 'de', 'es', 'fr', 'he-IL', 'hu', 'it', 'ja', 'ko', 'nl', 'pl', 'pt-BR', 'ru', 'tr', 'uk', 'zh')
foreach ($culture in $cultures) {
    $source = Join-Path $buildOutput "$culture\PromptWall.resources.dll"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing satellite assembly: $source"
    }
    $destination = Join-Path $installerOutput $culture
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'PromptWall.resources.dll') -Force
}

Write-Output "Prepared WiX sources from $buildOutput"
