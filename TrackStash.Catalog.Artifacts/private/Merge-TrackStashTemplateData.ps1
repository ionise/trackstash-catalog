<#
.SYNOPSIS
Merges template data into a YAML template.

.DESCRIPTION
This helper is reserved for future templating support and documents the
maintenance boundary for template merging logic.

.PARAMETER Template
The template data structure.

.PARAMETER Data
The data to merge into the template.
#>
function Merge-TrackStashTemplateData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Template,

        [Parameter(Mandatory)]
        [hashtable]$Data
    )

    throw [System.NotImplementedException]::new('Merge-TrackStashTemplateData is scaffolded but not yet implemented.')
}