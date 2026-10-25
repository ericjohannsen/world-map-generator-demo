#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath
)

# Rebuild context.txt from the requirement and design documentation.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\make-context.ps1
#
# Order: docs\requirements, then docs\design. Subfolders are included and each
# tree is sorted by full path so the output is deterministic. Written as UTF-8
# without a BOM.

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($OutputPath)) {
    $OutputPath = Join-Path $root 'context.txt'
}
$sources = @(
    (Join-Path $root 'docs\requirements'),
    (Join-Path $root 'docs\design')
)

foreach ($source in $sources) {
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Missing documentation directory: $source"
    }
}

$builder = New-Object System.Text.StringBuilder
$count = 0

foreach ($source in $sources) {
    $files = Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length).TrimStart([char[]]@('\', '/'))
        [void]$builder.AppendLine("===== $relative =====")
        [void]$builder.AppendLine([System.IO.File]::ReadAllText($file.FullName))
        $count++
    }
}

$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), $encoding)

Write-Host "Wrote $OutputPath ($count files)"
