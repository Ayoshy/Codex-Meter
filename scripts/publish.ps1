param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $SelfContained
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CodexUsageTray\CodexUsageTray.csproj'
$folderName = if ($SelfContained) { "$Runtime-self-contained" } else { $Runtime }
$output = Join-Path $root "artifacts\$folderName"
$rootPath = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
$outputPath = [System.IO.Path]::GetFullPath($output)

if (-not $outputPath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Le dossier de publication sort du workspace : $outputPath"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

$singleFile = $SelfContained.IsPresent.ToString().ToLowerInvariant()
$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=$singleFile `
    -p:IncludeNativeLibrariesForSelfExtract=$singleFile `
    --output $outputPath

Write-Host "Publié dans $outputPath"
