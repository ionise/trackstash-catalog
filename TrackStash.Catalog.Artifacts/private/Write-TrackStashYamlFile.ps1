<#
.SYNOPSIS
Writes a YAML artifact to disk.

.DESCRIPTION
Persists the generated YAML content to the requested file path and supports
ShouldProcess for safe preview usage.

.PARAMETER Path
Output file path.

.PARAMETER Content
YAML content to write.
#>
function Write-TrackStashYamlFile {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Content
    )

    if ($PSCmdlet.ShouldProcess($Path, 'Write YAML artifact')) {
        Set-Content -LiteralPath $Path -Value $Content -Encoding utf8
    }

    return $Path
}