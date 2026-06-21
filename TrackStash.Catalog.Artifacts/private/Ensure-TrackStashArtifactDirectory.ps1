<#
.SYNOPSIS
Ensures an artifact directory exists.

.DESCRIPTION
Creates the directory used for generated catalog artifacts when it is missing.

.PARAMETER Path
Directory path to create or verify.
#>
function Ensure-TrackStashArtifactDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    return $Path
}