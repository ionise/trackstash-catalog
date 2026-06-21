function Publish-TrackStashCatalogArtifact {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [string[]]$Path,

        [string]$Catalog,

        [string]$DbPath,

        [switch]$PassThru
    )

    begin {
        $artifactPaths = New-Object System.Collections.Generic.List[string]
        $results = New-Object System.Collections.Generic.List[object]
    }

    process {
        foreach ($incomingPath in $Path) {
            if ([string]::IsNullOrWhiteSpace($incomingPath)) {
                continue
            }

            if (-not (Test-Path -LiteralPath $incomingPath)) {
                throw "Artifact path not found: $incomingPath"
            }

            $item = Get-Item -LiteralPath $incomingPath
            if ($item.PSIsContainer) {
                $childArtifacts = Get-ChildItem -LiteralPath $item.FullName -Recurse -File |
                    Where-Object { $_.Extension -in @('.yaml', '.yml') }

                foreach ($child in $childArtifacts) {
                    $artifactPaths.Add($child.FullName)
                }
                continue
            }

            if ($item.Extension -notin @('.yaml', '.yml')) {
                throw "Unsupported artifact file extension: $($item.Extension)"
            }

            $artifactPaths.Add($item.FullName)
        }
    }

    end {
        foreach ($artifactPath in ($artifactPaths | Sort-Object -Unique)) {
            $validationOutput = Invoke-TrackStashCatalogCommand -Arguments @('validate-entity', '--file', $artifactPath, '--output', 'json')
            $validation = ($validationOutput -join [Environment]::NewLine) | ConvertFrom-Json
            if (-not $validation.Ok) {
                $errors = @($validation.Errors)
                if ($errors.Count -eq 0 -and $validation.Data) {
                    $issues = @($validation.Data.issues)
                    if ($issues.Count -gt 0) {
                        $errors = $issues | ForEach-Object { "$($_.severity): $($_.path) - $($_.message)" }
                    }
                }

                if ($errors.Count -eq 0) {
                    $errors = @("Validation failed for $artifactPath.")
                }

                throw ($errors -join [Environment]::NewLine)
            }

            if (-not $PSCmdlet.ShouldProcess($artifactPath, 'Apply catalog artifact')) {
                continue
            }

            $applyArgs = @('apply-entity', '--file', $artifactPath, '--output', 'json')
            if (-not [string]::IsNullOrWhiteSpace($Catalog)) {
                $applyArgs += @('--catalog', $Catalog)
            }
            if (-not [string]::IsNullOrWhiteSpace($DbPath)) {
                $applyArgs += @('--db-path', $DbPath)
            }

            $applyOutput = Invoke-TrackStashCatalogCommand -Arguments $applyArgs
            $applyResult = ($applyOutput -join [Environment]::NewLine) | ConvertFrom-Json
            if (-not $applyResult.Ok) {
                $errors = @($applyResult.Errors)
                if ($errors.Count -eq 0) {
                    $errors = @("Apply failed for $artifactPath.")
                }

                throw ($errors -join [Environment]::NewLine)
            }

            $data = $applyResult.Data
            $results.Add([pscustomobject]@{
                ArtifactPath = $artifactPath
                Kind = [string]$data.kind
                EntityId = [string]$data.entityId
                Mode = [string]$data.mode
                Success = $true
            })
        }

        if ($PassThru) {
            $results
        }
    }
}