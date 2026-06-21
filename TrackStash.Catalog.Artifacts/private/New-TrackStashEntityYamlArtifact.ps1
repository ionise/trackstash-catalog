<#
.SYNOPSIS
Creates a YAML artifact for a catalog entity.

.DESCRIPTION
Builds the common YAML envelope used by the public artifact cmdlets and writes
the file to the correct kind-specific folder using catalog-backed identity
resolution.

.PARAMETER Kind
Entity kind name.

.PARAMETER Name
Display name used for identity resolution.

.PARAMETER Id
Optional explicit entity ID.

.PARAMETER RootPath
Root folder where the artifact folder structure should be created.
#>
function New-TrackStashEntityYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind,

        [Parameter(Mandatory)]
        [string]$Name,

        [string]$Id,

        [string]$RootPath = (Get-Location).Path
    )

    $normalizedKind = $Kind.ToLowerInvariant()
    $identity = Resolve-TrackStashCatalogEntityIdentity -Value $Name
    $slug = $identity.slug
    $normalizedName = $identity.normalizedName

    if ([string]::IsNullOrWhiteSpace($Id)) {
        $prefix = switch ($normalizedKind) {
            'label' { 'lbl_' }
            'artist' { 'art_' }
            'release' { 'rel_' }
            'recording' { 'rec_' }
        }

        $Id = $prefix + $slug
    }

    $artifactPath = Resolve-TrackStashArtifactPath -Kind $normalizedKind -Slug $slug -RootPath $RootPath
    $null = Ensure-TrackStashArtifactDirectory -Path (Split-Path -Parent $artifactPath)

    $scalarKind = switch ($normalizedKind) {
        'label' { 'name' }
        'artist' { 'name' }
        'release' { 'title' }
        'recording' { 'title' }
    }

    $lines = @(
        'apiVersion: catalog.trackstash/v1',
        ('kind: {0}' -f ($normalizedKind.Substring(0, 1).ToUpperInvariant() + $normalizedKind.Substring(1))),
        'mode: replace',
        'metadata:',
        ('  id: {0}' -f (Format-TrackStashYamlScalar $Id)),
        'spec:',
        ('  id: {0}' -f (Format-TrackStashYamlScalar $Id)),
        ('  {0}: {1}' -f $scalarKind, (Format-TrackStashYamlScalar $Name)),
        ('  normalizedName: {0}' -f (Format-TrackStashYamlScalar $normalizedName))
    )

    if ($normalizedKind -in @('label', 'artist')) {
        $lines += ('  sortName: {0}' -f (Format-TrackStashYamlScalar $Name))
    }

    $content = $lines -join [Environment]::NewLine
    $null = Write-TrackStashYamlFile -Path $artifactPath -Content $content

    return [TrackStashCatalogEntityArtifact]::new(
        $normalizedKind,
        $Name,
        $normalizedName,
        $slug,
        $artifactPath,
        $content)
}