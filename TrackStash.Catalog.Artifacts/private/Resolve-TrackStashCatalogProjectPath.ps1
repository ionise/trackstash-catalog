function Resolve-TrackStashCatalogProjectPath {
    [CmdletBinding()]
    param()

    $command = Get-Command -Name 'trackstash-catalog' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command
    }

    $moduleRoot = Split-Path -Parent $PSScriptRoot
    $repoRoot = Split-Path -Parent $moduleRoot
    $projectPath = Join-Path $repoRoot 'src/TrackStash.Catalog/TrackStash.Catalog.csproj'

    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Unable to locate trackstash-catalog. Expected executable on PATH or project file at $projectPath."
    }

    return $projectPath
}