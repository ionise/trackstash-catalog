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