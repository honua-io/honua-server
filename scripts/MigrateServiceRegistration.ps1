# Service Registration Consolidation Migration Script
# Helps migrate existing ServiceCollectionExtensions files to use the consolidated framework

param(
    [string]$ProjectPath = ".",
    [switch]$WhatIf = $false,
    [switch]$BackupOriginal = $true
)

Write-Host "Service Registration Consolidation Migration Script" -ForegroundColor Green
Write-Host "Project Path: $ProjectPath" -ForegroundColor Cyan
Write-Host "What-If Mode: $WhatIf" -ForegroundColor Cyan
Write-Host "Backup Original: $BackupOriginal" -ForegroundColor Cyan

# Find all ServiceCollectionExtensions files
$serviceCollectionFiles = Get-ChildItem -Path $ProjectPath -Recurse -Name "ServiceCollectionExtensions.cs" -File

Write-Host "`nFound $($serviceCollectionFiles.Count) ServiceCollectionExtensions files:" -ForegroundColor Yellow
$serviceCollectionFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor White }

# Analyze duplication patterns
Write-Host "`nAnalyzing duplication patterns..." -ForegroundColor Yellow

$totalRegistrationLines = 0
$duplicatePatterns = @{}

foreach ($file in $serviceCollectionFiles) {
    $filePath = Join-Path $ProjectPath $file
    $content = Get-Content $filePath -Raw

    # Count service registration lines
    $registrationMatches = [regex]::Matches($content, 'services\.(Add|TryAdd)(Scoped|Singleton|Transient)')
    $registrationCount = $registrationMatches.Count
    $totalRegistrationLines += $registrationCount

    Write-Host "  $file`: $registrationCount registration lines" -ForegroundColor White

    # Identify common patterns
    if ($content -match 'services\.TryAddScoped<.*,.*>()') {
        $duplicatePatterns['TryAddScoped'] = ($duplicatePatterns['TryAddScoped'] ?? 0) + 1
    }
    if ($content -match 'services\.AddOptions<.*>()') {
        $duplicatePatterns['AddOptions'] = ($duplicatePatterns['AddOptions'] ?? 0) + 1
    }
    if ($content -match 'serviceProvider => new.*\(') {
        $duplicatePatterns['FactoryPattern'] = ($duplicatePatterns['FactoryPattern'] ?? 0) + 1
    }
    if ($content -match 'schemaName') {
        $duplicatePatterns['SchemaPattern'] = ($duplicatePatterns['SchemaPattern'] ?? 0) + 1
    }
    if ($content -match 'provider\.GetRequiredService') {
        $duplicatePatterns['DependencyInjection'] = ($duplicatePatterns['DependencyInjection'] ?? 0) + 1
    }
}

Write-Host "`nDuplication Analysis Results:" -ForegroundColor Yellow
Write-Host "  Total registration lines: $totalRegistrationLines" -ForegroundColor White
Write-Host "  Duplicate patterns found:" -ForegroundColor White
$duplicatePatterns.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host "    $($_.Key): $($_.Value) files" -ForegroundColor Cyan
}

$estimatedReduction = [math]::Round(($totalRegistrationLines * 0.85))
Write-Host "  Estimated line reduction: ~$estimatedReduction lines (85%)" -ForegroundColor Green

# Generate migration recommendations
Write-Host "`nMigration Recommendations:" -ForegroundColor Yellow

foreach ($file in $serviceCollectionFiles) {
    $filePath = Join-Path $ProjectPath $file
    $content = Get-Content $filePath -Raw

    Write-Host "`n  $file`:" -ForegroundColor White

    # Analyze file content and provide recommendations
    if ($file -match "Core.*Features.*(AutoDocs|Import|Styling)") {
        Write-Host "    Recommendation: Use AddSimpleCoreFeature pattern" -ForegroundColor Green
        Write-Host "    Example: services.AddSimpleCoreFeature<IService, Implementation>()" -ForegroundColor Gray
    }
    elseif ($file -match "Postgres.*Features") {
        Write-Host "    Recommendation: Use AddPostgresFeatureServices or AddSchemaBasedService pattern" -ForegroundColor Green
        Write-Host "    Example: services.AddSchemaBasedService<IService, Implementation>(schemaName)" -ForegroundColor Gray
    }
    elseif ($content -match "Registry|Provider.*Factory") {
        Write-Host "    Recommendation: Use AddProviderRegistry pattern" -ForegroundColor Green
        Write-Host "    Example: services.AddProviderRegistry<IProvider, Options>()" -ForegroundColor Gray
    }
    elseif ($content -match "FeatureStore|IFeatureReader|IFeatureWriter") {
        Write-Host "    Recommendation: Use AddFeatureStoreServices pattern" -ForegroundColor Green
        Write-Host "    Example: services.AddFeatureStoreServices<Store>(schema, interfaces...)" -ForegroundColor Gray
    }
    else {
        Write-Host "    Recommendation: Use ServiceRegistrationHelpers methods" -ForegroundColor Green
        Write-Host "    Example: services.AddScopedService<IService, Implementation>()" -ForegroundColor Gray
    }

    # Check for configuration validation
    if ($content -match "AddOptions.*Bind.*ValidateOnStart") {
        Write-Host "    Note: Replace with AddValidatedConfiguration pattern" -ForegroundColor Yellow
    }

    # Check for object pooling
    if ($content -match "ObjectPool|StringBuilder|Dictionary") {
        Write-Host "    Note: Use AddPerformanceOptimizedObjectPools()" -ForegroundColor Yellow
    }
}

# Generate sample consolidated file for the largest ServiceCollectionExtensions
$largestFile = $serviceCollectionFiles | ForEach-Object {
    $filePath = Join-Path $ProjectPath $_
    $lineCount = (Get-Content $filePath | Measure-Object -Line).Lines
    [PSCustomObject]@{
        File = $_
        Path = $filePath
        Lines = $lineCount
    }
} | Sort-Object Lines -Descending | Select-Object -First 1

if ($largestFile) {
    Write-Host "`nGenerating sample consolidated version of largest file: $($largestFile.File)" -ForegroundColor Yellow

    $originalContent = Get-Content $largestFile.Path -Raw
    $consolidatedSample = @"
// CONSOLIDATED VERSION - SAMPLE
// Original file: $($largestFile.File) ($($largestFile.Lines) lines)
// Consolidated version: ~$([math]::Round($largestFile.Lines * 0.15)) lines (85% reduction)

using Honua.Core.Features.Infrastructure.ServiceRegistration;
// ... other usings

namespace /* Original Namespace */;

/// <summary>
/// Consolidated service collection extensions using the new framework.
/// Replaces $($largestFile.Lines) lines with ~$([math]::Round($largestFile.Lines * 0.15)) lines.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConsolidatedServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string? schemaName = null)
    {
        // Example consolidated registrations:

        // Simple services
        services
            .AddScopedService<IService1, Implementation1>()
            .AddScopedService<IService2, Implementation2>()
            .AddSingletonService<ISingleton, SingletonImpl>();

        // Schema-based services
        services
            .AddSchemaBasedService<IStore1, PostgresStore1>(schemaName)
            .AddSchemaBasedService<IStore2, PostgresStore2>(schemaName);

        // Configuration with validation
        services.AddValidatedConfiguration<MyOptions, MyOptionsValidator>(
            configuration.GetSection("MySection"));

        // Provider pattern
        services.AddProviderRegistry<IProvider, ProviderOptions>();

        // Feature store with segregated interfaces
        services.AddFeatureStoreServices<MainStore>(schemaName,
            typeof(IReader), typeof(IWriter), typeof(ITileProvider));

        // Performance optimizations
        services.AddPerformanceOptimizedObjectPools();

        return services;
    }
}
"@

    $samplePath = $largestFile.Path -replace '\.cs$', '.Consolidated.Sample.cs'

    if (-not $WhatIf) {
        $consolidatedSample | Out-File -FilePath $samplePath -Encoding UTF8
        Write-Host "  Sample consolidated version written to: $samplePath" -ForegroundColor Green
    }
    else {
        Write-Host "  [WHAT-IF] Would write sample to: $samplePath" -ForegroundColor Cyan
    }
}

# Create backup if requested
if ($BackupOriginal -and -not $WhatIf) {
    Write-Host "`nCreating backups..." -ForegroundColor Yellow
    foreach ($file in $serviceCollectionFiles) {
        $filePath = Join-Path $ProjectPath $file
        $backupPath = $filePath -replace '\.cs$', '.Original.cs'
        Copy-Item $filePath $backupPath
        Write-Host "  Backed up: $file -> $($backupPath | Split-Path -Leaf)" -ForegroundColor White
    }
}

# Summary
Write-Host "`n" + "="*80 -ForegroundColor Green
Write-Host "MIGRATION SUMMARY" -ForegroundColor Green
Write-Host "="*80 -ForegroundColor Green
Write-Host "Files to migrate: $($serviceCollectionFiles.Count)" -ForegroundColor White
Write-Host "Total registration lines: $totalRegistrationLines" -ForegroundColor White
Write-Host "Estimated line reduction: ~$estimatedReduction lines (85%)" -ForegroundColor Green
Write-Host "New framework location: Honua.Core.Features.Infrastructure.ServiceRegistration" -ForegroundColor Cyan

Write-Host "`nNext Steps:" -ForegroundColor Yellow
Write-Host "1. Review the consolidated framework classes" -ForegroundColor White
Write-Host "2. Create consolidated versions using the provided patterns" -ForegroundColor White
Write-Host "3. Update existing registrations to use consolidated methods" -ForegroundColor White
Write-Host "4. Test consolidated registrations thoroughly" -ForegroundColor White
Write-Host "5. Remove original files once migration is complete" -ForegroundColor White

Write-Host "`nDocumentation: docs/developer/SERVICE_REGISTRATION_CONSOLIDATION.md" -ForegroundColor Cyan
Write-Host "="*80 -ForegroundColor Green