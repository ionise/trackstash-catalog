<#
.SYNOPSIS
Resolves a catalog artifact path.

.DESCRIPTION
Builds the target file path for a generated artifact from its kind, slug, and
root path.

.PARAMETER Kind
Entity kind name.

.PARAMETER Slug
Entity slug used for the file name.

.PARAMETER RootPath
Root folder for generated artifacts.
#>
function Resolve-TrackStashArtifactPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind,

        [Parameter(Mandatory)]
        [string]$Slug,

        [string]$RootPath = (Get-Location).Path
    )

    $kindFolder = $Kind.ToLowerInvariant()
    return (Join-Path (Join-Path $RootPath $kindFolder) "$Slug.yaml")
}