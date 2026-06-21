<#
.SYNOPSIS
Converts an entity envelope object into a YAML artifact file.

.DESCRIPTION
Accepts TrackStash catalog entity envelope objects from the pipeline, converts
them into YAML, and writes them into the standard kind/slug artifact layout.

.PARAMETER InputObject
Entity envelope object.

.PARAMETER Path
Optional explicit output file path.

.PARAMETER RootPath
Root folder used when Path is not provided.
#>
function ConvertTo-TrackStashCatalogYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [object]$InputObject,

        [string]$Path,

        [string]$RootPath = (Get-Location).Path
    )

    process {
        if ($null -eq $InputObject) {
            return
        }

        $apiVersion = [string]$InputObject.apiVersion
        $kindRaw = [string]$InputObject.kind
        $mode = [string]$InputObject.mode
        $metadata = $InputObject.metadata
        $spec = $InputObject.spec

        if ([string]::IsNullOrWhiteSpace($apiVersion) -or [string]::IsNullOrWhiteSpace($kindRaw) -or $null -eq $spec) {
            throw 'Input object must include apiVersion, kind, and spec properties.'
        }

        if ([string]::IsNullOrWhiteSpace($mode)) {
            $mode = 'replace'
            $InputObject | Add-Member -NotePropertyName mode -NotePropertyValue $mode -Force
        }

        $normalizedKind = $kindRaw.ToLowerInvariant()
        $entityId = [string]$spec.id
        if ([string]::IsNullOrWhiteSpace($entityId) -and $null -ne $metadata) {
            $entityId = [string]$metadata.id
            if (-not [string]::IsNullOrWhiteSpace($entityId)) {
                $spec | Add-Member -NotePropertyName id -NotePropertyValue $entityId -Force
            }
        }

        if ([string]::IsNullOrWhiteSpace($entityId)) {
            throw 'Entity id is required on spec.id or metadata.id before YAML conversion.'
        }

        if ($null -eq $metadata) {
            $metadata = [ordered]@{ id = $entityId }
            $InputObject | Add-Member -NotePropertyName metadata -NotePropertyValue $metadata -Force
        }
        elseif ([string]::IsNullOrWhiteSpace([string]$metadata.id)) {
            $metadata | Add-Member -NotePropertyName id -NotePropertyValue $entityId -Force
        }

        $name = [string]$spec.name
        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = [string]$spec.title
        }

        $normalizedName = [string]$spec.normalizedName

        $slug = $null
        if ($entityId -match '^[a-z]{3}_(.+)$') {
            $slug = $Matches[1]
        }

        if ([string]::IsNullOrWhiteSpace($slug) -and -not [string]::IsNullOrWhiteSpace($name)) {
            try {
                $slug = [string](Resolve-TrackStashCatalogEntityIdentity -Value $name).slug
            }
            catch {
                $slug = (($name -replace '[^\p{L}\p{Nd}]+', '-').Trim('-').ToLowerInvariant() -replace '-{2,}', '-')
            }
        }

        if ([string]::IsNullOrWhiteSpace($slug)) {
            throw 'Unable to resolve artifact slug from id or entity name.'
        }

        $artifactPath = $Path
        if ([string]::IsNullOrWhiteSpace($artifactPath)) {
            $artifactPath = Resolve-TrackStashArtifactPath -Kind $normalizedKind -Slug $slug -RootPath $RootPath
        }

        $null = Ensure-TrackStashArtifactDirectory -Path (Split-Path -Parent $artifactPath)

        $content = ConvertTo-TrackStashYamlDocument -InputObject $InputObject
        $null = Write-TrackStashYamlFile -Path $artifactPath -Content $content

        return [TrackStashCatalogEntityArtifact]::new(
            $normalizedKind,
            [string]$name,
            [string]$normalizedName,
            [string]$slug,
            [string]$artifactPath,
            [string]$content)
    }
}