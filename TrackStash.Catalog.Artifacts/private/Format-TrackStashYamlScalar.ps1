<#
.SYNOPSIS
Formats a value as a YAML scalar.

.DESCRIPTION
Produces a compact YAML scalar representation suitable for generated artifact
documents.

.PARAMETER Value
The scalar value to format.
#>
function Format-TrackStashYamlScalar {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return 'null'
    }

    if ($Value.Length -eq 0) {
        return "''"
    }

    if ($Value -match '^[A-Za-z0-9_.-]+$') {
        return $Value
    }

    $escaped = $Value.Replace('\\', '\\\\').Replace('"', '\\"')
    return '"{0}"' -f $escaped
}