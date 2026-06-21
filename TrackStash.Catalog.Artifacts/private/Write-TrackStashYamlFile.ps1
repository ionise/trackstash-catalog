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