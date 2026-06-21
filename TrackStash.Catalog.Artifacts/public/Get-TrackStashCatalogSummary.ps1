<#
.SYNOPSIS
Gets a summary of catalog state.

.DESCRIPTION
This cmdlet is the public summary/discovery entry point for the PowerShell
module and is intended to remain read-only.
#>
function Get-TrackStashCatalogSummary {
    [CmdletBinding()]
    param()

    $output = Invoke-TrackStashCatalogCommand -Arguments @('summary', '--output', 'json')
    $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json

    if (-not $result.Ok) {
        $errors = @($result.Errors)
        if ($errors.Count -eq 0) {
            $errors = @('Catalog summary request failed.')
        }

        throw ($errors -join [Environment]::NewLine)
    }

    return $result.Data
}