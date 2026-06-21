function Resolve-TrackStashCatalogEntityIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $output = Invoke-TrackStashCatalogCommand -Arguments @('resolve-entity-identity', '--value', $Value, '--output', 'json')

    $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if (-not $result.Ok) {
        $errors = @($result.Errors)
        if ($errors.Count -eq 0) {
            $errors = @('Catalog identity resolution failed.')
        }

        throw ($errors -join [Environment]::NewLine)
    }

    return $result.Data
}