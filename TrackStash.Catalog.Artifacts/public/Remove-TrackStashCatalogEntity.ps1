<#
.SYNOPSIS
Deletes a catalog entity.

.DESCRIPTION
Wraps the catalog delete-entity command so PowerShell workflows can perform
delete operations without dropping to the CLI.

.PARAMETER Id
Entity ID to delete.

.PARAMETER Kind
Entity kind. If omitted, kind is inferred from the ID prefix.
#>
function Remove-TrackStashCatalogEntity {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Id,

        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind,

        [string]$Catalog,

        [string]$DbPath,

        [string]$DeletedBy,

        [string]$Reason,

        [switch]$PassThru
    )

    process {
        if ([string]::IsNullOrWhiteSpace($Kind)) {
            if ($Id.StartsWith('lbl_', [StringComparison]::OrdinalIgnoreCase)) {
                $Kind = 'label'
            }
            elseif ($Id.StartsWith('art_', [StringComparison]::OrdinalIgnoreCase)) {
                $Kind = 'artist'
            }
            elseif ($Id.StartsWith('rel_', [StringComparison]::OrdinalIgnoreCase)) {
                $Kind = 'release'
            }
            elseif ($Id.StartsWith('rec_', [StringComparison]::OrdinalIgnoreCase)) {
                $Kind = 'recording'
            }
            else {
                throw "Unable to infer entity kind from Id '$Id'. Provide -Kind label|artist|release|recording."
            }
        }

        if (-not $PSCmdlet.ShouldProcess($Id, "Delete $Kind entity")) {
            return
        }

        $args = @('delete-entity', '--type', $Kind, '--id', $Id, '--output', 'json')

        if (-not [string]::IsNullOrWhiteSpace($Catalog)) {
            $args += @('--catalog', $Catalog)
        }

        if (-not [string]::IsNullOrWhiteSpace($DbPath)) {
            $args += @('--db-path', $DbPath)
        }

        if (-not [string]::IsNullOrWhiteSpace($DeletedBy)) {
            $args += @('--deleted-by', $DeletedBy)
        }

        if (-not [string]::IsNullOrWhiteSpace($Reason)) {
            $args += @('--reason', $Reason)
        }

        $output = Invoke-TrackStashCatalogCommand -Arguments $args
        $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json

        if (-not $result.Ok) {
            $errors = @($result.Errors)
            if ($errors.Count -eq 0 -and $result.Data) {
                $errorMessage = [string]$result.Data.errorMessage
                if (-not [string]::IsNullOrWhiteSpace($errorMessage)) {
                    $errors = @($errorMessage)
                }
            }

            if ($errors.Count -eq 0) {
                $errors = @("Delete failed for kind '$Kind' and id '$Id'.")
            }

            throw ($errors -join [Environment]::NewLine)
        }

        if ($PassThru) {
            return $result.Data
        }
    }
}