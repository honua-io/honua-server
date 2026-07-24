param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
)

$forbiddenCommand = [regex]::new('(?i)\bcurl(?:\.exe)?\b')
$violations = [System.Collections.Generic.List[string]]::new()

Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.md' |
    Sort-Object FullName |
    ForEach-Object {
        $path = $_.FullName
        $relativePath = $path.Substring($RepositoryRoot.TrimEnd('\', '/').Length + 1)
        $lineNumber = 0

        foreach ($line in [System.IO.File]::ReadLines($path)) {
            $lineNumber++
            if ($forbiddenCommand.IsMatch($line)) {
                $violations.Add("${relativePath}:${lineNumber}:$($line.Trim())")
            }
        }
    }

if ($violations.Count -gt 0) {
    Write-Error "Markdown command policy failed. Use a supported Honua CLI/SDK or link to the generated OpenAPI contract."
    $violations | ForEach-Object { Write-Host "::error::$_" }
    exit 1
}

Write-Host "Markdown command policy passed."
