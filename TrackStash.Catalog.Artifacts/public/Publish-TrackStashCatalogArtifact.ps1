<#
.SYNOPSIS
Validates and publishes catalog YAML artifacts.

.DESCRIPTION
Reads one or more YAML artifact files or directories, validates each artifact
with the catalog CLI, and applies them through the catalog publish/apply path.

.PARAMETER Path
One file path or directory path containing YAML artifacts.

.PARAMETER Catalog
Optional catalog name to pass through to the catalog CLI.

.PARAMETER DbPath
Optional SQLite database path to pass through to the catalog CLI.

.PARAMETER PassThru
Returns a summary object for each successfully applied artifact.

.PARAMETER RetryCount
Number of retries for transient SQLite lock errors.

.PARAMETER RetryDelaySeconds
Delay between retries when SQLite lock errors are encountered.
#>
function Publish-TrackStashCatalogArtifact {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [string[]]$Path,

        [string]$Catalog,

        [string]$DbPath,

        [ValidateRange(0, 20)]
        [int]$RetryCount = 5,

        [ValidateRange(0, 30)]
        [int]$RetryDelaySeconds = 1,

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

            $applyResult = $null
            $lastErrors = @()
            for ($attempt = 0; $attempt -le $RetryCount; $attempt++) {
                $applyOutput = Invoke-TrackStashCatalogCommand -Arguments $applyArgs
                $applyResult = ($applyOutput -join [Environment]::NewLine) | ConvertFrom-Json

                if ($applyResult.Ok) {
                    break
                }

                $errors = @($applyResult.Errors)
                if ($errors.Count -eq 0) {
                    $errors = @("Apply failed for $artifactPath.")
                }
                $lastErrors = $errors

                $isSqliteLock = ($errors -join ' ') -match 'SQLite Error 5|database is locked'
                if (-not $isSqliteLock -or $attempt -ge $RetryCount) {
                    break
                }

                Write-Verbose "SQLite lock detected for '$artifactPath'. Retry $($attempt + 1) of $RetryCount in ${RetryDelaySeconds}s..."
                Start-Sleep -Seconds $RetryDelaySeconds
            }

            if (-not $applyResult.Ok) {
                throw ($lastErrors -join [Environment]::NewLine)
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