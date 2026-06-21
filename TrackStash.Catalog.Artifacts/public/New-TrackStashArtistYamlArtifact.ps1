<#
.SYNOPSIS
Creates a YAML artifact for an artist.

.DESCRIPTION
Generates a catalog-ready YAML document for an artist, using catalog-backed
slug and normalized-name resolution.

.PARAMETER Name
Artist display name.

.PARAMETER Id
Optional explicit entity ID.

.PARAMETER RootPath
Root folder where the artifact folder structure should be created.
#>
function New-TrackStashArtistYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$Id,

        [string]$RootPath = (Get-Location).Path
    )

    return New-TrackStashEntityYamlArtifact -Kind artist -Name $Name -Id $Id -RootPath $RootPath
}