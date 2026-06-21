function Resolve-TrackStashCatalogEntityIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $command = Get-Command -Name 'trackstash-catalog' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $output = & $command.Source resolve-entity-identity --value $Value --output json
    }
    else {
        $moduleRoot = Split-Path -Parent $PSScriptRoot
        $repoRoot = Split-Path -Parent $moduleRoot
        $projectPath = Join-Path $repoRoot 'src/TrackStash.Catalog/TrackStash.Catalog.csproj'

        if (-not (Test-Path -LiteralPath $projectPath)) {
            throw "Unable to locate trackstash-catalog. Expected executable on PATH or project file at $projectPath."
        }

        $output = & dotnet run --project $projectPath -- resolve-entity-identity --value $Value --output json
    }

    $result = $output | ConvertFrom-Json
    if (-not $result.Ok) {
        $errors = @($result.Errors)
        if ($errors.Count -eq 0) {
            $errors = @('Catalog identity resolution failed.')
        }

        throw ($errors -join [Environment]::NewLine)
    }

    return $result.Data
}