<#
.SYNOPSIS
Converts an object into a TrackStash YAML document.

.DESCRIPTION
This private helper is reserved for future YAML serialization support and is
documented now so its maintenance purpose is explicit.

.PARAMETER InputObject
The object to serialize.
#>
function ConvertTo-TrackStashYamlDocument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$InputObject
    )

    throw [System.NotImplementedException]::new('ConvertTo-TrackStashYamlDocument is scaffolded but not yet implemented.')
}