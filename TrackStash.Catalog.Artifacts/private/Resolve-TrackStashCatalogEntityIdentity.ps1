<#
.SYNOPSIS
Resolves catalog identity details for a value.

.DESCRIPTION
Calls the catalog CLI to obtain the normalized name and slug for a display
value so PowerShell can use the same identity rules as the catalog layer.

.PARAMETER Value
The source display value to resolve.
#>
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