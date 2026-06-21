<#
.SYNOPSIS
Creates a YAML artifact for a release.

.DESCRIPTION
Generates a catalog-ready YAML document for a release, using catalog-backed
slug and normalized-name resolution.

.PARAMETER Name
Release display name or title.

.PARAMETER Id
Optional explicit entity ID.

.PARAMETER RootPath
Root folder where the artifact folder structure should be created.
#>
function New-TrackStashReleaseYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$Id,

        [string]$RootPath = (Get-Location).Path
    )

    return New-TrackStashEntityYamlArtifact -Kind release -Name $Name -Id $Id -RootPath $RootPath
}