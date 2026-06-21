<#
.SYNOPSIS
Creates a YAML artifact for a label.

.DESCRIPTION
Generates a catalog-ready YAML document for a label, using catalog-backed
slug and normalized-name resolution.

.PARAMETER Name
Label display name.

.PARAMETER Id
Optional explicit entity ID.

.PARAMETER RootPath
Root folder where the artifact folder structure should be created.
#>
function New-TrackStashLabelYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$Id,

        [string]$RootPath = (Get-Location).Path
    )

    return New-TrackStashEntityYamlArtifact -Kind label -Name $Name -Id $Id -RootPath $RootPath
}