<#
.SYNOPSIS
Gets one catalog entity by ID.

.DESCRIPTION
This is the single-entity read path for catalog discovery and inspection. The
cmdlet is intentionally separate from search so direct lookups stay explicit.

.PARAMETER Id
The catalog entity ID to retrieve.

.PARAMETER Kind
Optional entity kind. If omitted, kind is inferred from ID prefix.

.PARAMETER Format
Entity output format. Currently supports yaml.

.PARAMETER Raw
Returns only raw content when present.
#>
function Get-TrackStashCatalogEntity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Id,

        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind,

        [ValidateSet('yaml')]
        [string]$Format = 'yaml',

        [switch]$Raw
    )

    if ([string]::IsNullOrWhiteSpace($Kind)) {
        if ($Id.StartsWith('lbl_', [StringComparison]::OrdinalIgnoreCase)) {
            $Kind = 'label'
        }
        elseif ($Id.StartsWith('art_', [StringComparison]::OrdinalIgnoreCase)) {
            $Kind = 'artist'
        }
        elseif ($Id.StartsWith('rel_', [StringComparison]::OrdinalIgnoreCase)) {
            $Kind = 'release'
        }
        elseif ($Id.StartsWith('rec_', [StringComparison]::OrdinalIgnoreCase)) {
            $Kind = 'recording'
        }
        else {
            throw "Unable to infer entity kind from Id '$Id'. Provide -Kind label|artist|release|recording."
        }
    }

    $args = @(
        'get-entity',
        '--type', $Kind,
        '--id', $Id,
        '--format', $Format,
        '--output', 'json'
    )

    $output = Invoke-TrackStashCatalogCommand -Arguments $args
    $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json

    if (-not $result.Ok) {
        $errors = @($result.Errors)
        if ($errors.Count -eq 0) {
            $errors = @("Get entity failed for kind '$Kind' and id '$Id'.")
        }

        throw ($errors -join [Environment]::NewLine)
    }

    if ($Raw) {
        return $result.Data.content
    }

    return $result.Data
}