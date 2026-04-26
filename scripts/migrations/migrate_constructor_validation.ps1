# Constructor Validation Migration Script
# Automatically refactors constructor null validation patterns to use the new validation framework

param(
    [string]$ProjectPath = ".",
    [switch]$DryRun = $false,
    [switch]$Verbose = $false
)

Write-Host "🔧 Constructor Validation Migration Script" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green

if ($DryRun) {
    Write-Host "🔍 DRY RUN MODE - No files will be modified" -ForegroundColor Yellow
}

# Patterns to detect and replace
$patterns = @{
    # Basic null check pattern
    'BasicNullCheck' = @{
        'Pattern' = '(\w+)\s*=\s*(\w+)\s*\?\?\s*throw\s+new\s+ArgumentNullException\(nameof\((\w+)\)\);'
        'Replacement' = '$1 = $2.ThrowIfNull();'
        'RequiresUsing' = 'Honua.Core.Features.Infrastructure.Validation'
    }

    # IOptions pattern
    'OptionsPattern' = @{
        'Pattern' = '(\w+)\s*=\s*(\w+)\?\.Value\s*\?\?\s*throw\s+new\s+ArgumentNullException\(nameof\((\w+)\)\);'
        'Replacement' = '$1 = $2.ValidateAndGetValue();'
        'RequiresUsing' = 'Honua.Core.Features.Infrastructure.Validation'
    }

    # Connection provider + logger pattern (most common)
    'ConnectionProviderLogger' = @{
        'Pattern' = '_connectionProvider\s*=\s*connectionProvider\s*\?\?\s*throw\s+new\s+ArgumentNullException\(nameof\(connectionProvider\)\);\s*_logger\s*=\s*logger\s*\?\?\s*throw\s+new\s+ArgumentNullException\(nameof\(logger\)\);'
        'Replacement' = 'var (validatedConnectionProvider, validatedLogger) = ServiceValidationHelpers.ValidateServiceDependencies(connectionProvider, logger);' + [Environment]::NewLine + '        _connectionProvider = validatedConnectionProvider;' + [Environment]::NewLine + '        _logger = validatedLogger;'
        'RequiresUsing' = 'Honua.Core.Features.Infrastructure.Validation'
    }
}

function Test-NeedsValidationUsing {
    param([string]$fileContent)

    return $fileContent -notmatch "using\s+Honua\.Core\.Features\.Infrastructure\.Validation"
}

function Add-ValidationUsing {
    param([string]$fileContent)

    # Find the last using statement and add after it
    if ($fileContent -match "(using\s+[^;]+;)(?=\s*\n\s*(?:namespace|public|internal))") {
        $lastUsing = $matches[0]
        $insertPosition = $fileContent.IndexOf($lastUsing) + $lastUsing.Length
        $newUsing = [Environment]::NewLine + "using Honua.Core.Features.Infrastructure.Validation;"
        return $fileContent.Insert($insertPosition, $newUsing)
    }

    return $fileContent
}

function Get-CSharpFiles {
    param([string]$path)

    return Get-ChildItem -Path $path -Recurse -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch "(bin|obj|\.git)" }
}

function Test-ContainsValidationPattern {
    param([string]$content)

    foreach ($pattern in $patterns.Values) {
        if ($content -match $pattern.Pattern) {
            return $true
        }
    }
    return $false
}

function Invoke-PatternReplacement {
    param(
        [string]$content,
        [string]$filePath
    )

    $modified = $false
    $newContent = $content
    $appliedPatterns = @()

    foreach ($patternName in $patterns.Keys) {
        $pattern = $patterns[$patternName]

        if ($newContent -match $pattern.Pattern) {
            Write-Host "  📝 Applying pattern: $patternName" -ForegroundColor Blue
            $newContent = $newContent -replace $pattern.Pattern, $pattern.Replacement
            $modified = $true
            $appliedPatterns += $patternName
        }
    }

    # Add using statement if needed and patterns were applied
    if ($modified -and (Test-NeedsValidationUsing $newContent)) {
        Write-Host "  📦 Adding validation using statement" -ForegroundColor Blue
        $newContent = Add-ValidationUsing $newContent
    }

    return @{
        'Content' = $newContent
        'Modified' = $modified
        'Patterns' = $appliedPatterns
    }
}

# Main migration logic
$totalFiles = 0
$modifiedFiles = 0
$totalPatterns = 0

Write-Host "🔍 Scanning for C# files..." -ForegroundColor Cyan

$csharpFiles = Get-CSharpFiles $ProjectPath
$filesToProcess = $csharpFiles | Where-Object {
    $content = Get-Content $_.FullName -Raw
    Test-ContainsValidationPattern $content
}

Write-Host "📊 Found $($csharpFiles.Count) C# files, $($filesToProcess.Count) contain validation patterns" -ForegroundColor Cyan

foreach ($file in $filesToProcess) {
    $totalFiles++

    Write-Host "🔄 Processing: $($file.Name)" -ForegroundColor White

    try {
        $content = Get-Content $file.FullName -Raw
        $result = Invoke-PatternReplacement $content $file.FullName

        if ($result.Modified) {
            $modifiedFiles++
            $totalPatterns += $result.Patterns.Count

            Write-Host "  ✅ Modified - Applied patterns: $($result.Patterns -join ', ')" -ForegroundColor Green

            if (-not $DryRun) {
                Set-Content -Path $file.FullName -Value $result.Content -NoNewline
                Write-Host "  💾 File saved" -ForegroundColor Green
            }
        } else {
            Write-Host "  ⏭️ No changes needed" -ForegroundColor Yellow
        }

        if ($Verbose) {
            Write-Host "  🔍 File path: $($file.FullName)" -ForegroundColor Gray
        }
    }
    catch {
        Write-Host "  ❌ Error processing file: $_" -ForegroundColor Red
    }
}

Write-Host "" -ForegroundColor White
Write-Host "📈 Migration Summary" -ForegroundColor Green
Write-Host "===================" -ForegroundColor Green
Write-Host "Files processed: $totalFiles" -ForegroundColor White
Write-Host "Files modified: $modifiedFiles" -ForegroundColor Green
Write-Host "Patterns applied: $totalPatterns" -ForegroundColor Green

if ($DryRun) {
    Write-Host "" -ForegroundColor White
    Write-Host "🚀 To apply changes, run without -DryRun flag" -ForegroundColor Yellow
    Write-Host "Example: .\migrate_constructor_validation.ps1 -ProjectPath 'src/'" -ForegroundColor Gray
} else {
    Write-Host "" -ForegroundColor White
    Write-Host "✅ Migration completed!" -ForegroundColor Green
    Write-Host "🧪 Run tests to verify no behavioral changes" -ForegroundColor Yellow
}

Write-Host "" -ForegroundColor White
Write-Host "📋 Next Steps:" -ForegroundColor Cyan
Write-Host "1. Review modified files for correctness" -ForegroundColor White
Write-Host "2. Run full test suite: dotnet test" -ForegroundColor White
Write-Host "3. Check build: dotnet build" -ForegroundColor White
Write-Host "4. Code review changes before commit" -ForegroundColor White