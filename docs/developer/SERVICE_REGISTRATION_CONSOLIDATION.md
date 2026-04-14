# Service Registration Consolidation Framework

## Overview

The Service Registration Consolidation Framework eliminates duplication across the 8+ ServiceCollectionExtensions files that previously contained 150+ nearly identical service registration lines. This framework provides reusable patterns and reduces registration code by ~85%.

## Problem Solved

**Before Consolidation:**
- 8 separate `ServiceCollectionExtensions.cs` files
- 150+ service registration lines with 85% duplication
- Inconsistent configuration and validation patterns
- Copy-paste registration code across features
- Difficult to maintain and modify registration logic

**After Consolidation:**
- Single framework with reusable patterns
- ~20 lines of registration + configuration
- Consistent validation and configuration approaches
- Easy to maintain and extend
- Type-safe and testable registration patterns

## Framework Components

### 1. Base Framework Classes

#### `FeatureServiceCollectionExtensions`
Abstract base class for feature-specific service registration:

```csharp
public abstract class FeatureServiceCollectionExtensions
{
    protected abstract string ConfigurationSectionName { get; }
    protected abstract string FeatureName { get; }
    
    public IServiceCollection AddFeatureServices(
        IServiceCollection services,
        IConfiguration configuration,
        string? schemaName = null)
}
```

#### `ServiceRegistrationHelpers`
Static helpers for common registration patterns:

```csharp
// Simple service registration
services.AddScopedService<IInterface, Implementation>();

// Factory-based registration
services.AddScopedService<IService>(provider => new Service(...));

// Configuration with validation
services.AddConfigurationOptions<TOptions, TValidator>(configSection);

// Provider registry pattern
services.AddProviderRegistry<IProvider, TOptions>();

// Schema-based services
services.AddSchemaBasedService<IInterface, Implementation>(schemaName);

// Segregated interfaces (feature store pattern)
services.AddSegregatedInterfaces<Implementation>(typeof(IRead), typeof(IWrite));

// Read-only implementations
services.AddReadOnlyImplementations(
    (typeof(IWrite), typeof(ReadOnlyWrite)));
```

### 2. Common Patterns

#### `ServiceRegistrationPatterns`
Higher-level patterns for common scenarios:

```csharp
// PostgreSQL services with schema
services.AddPostgresFeatureServices<Store, IInterface>(schemaName);

// Feature store with multiple interfaces
services.AddFeatureStoreServices<Store>(schemaName,
    typeof(IReader), typeof(IWriter), typeof(ITileProvider));

// Performance optimizations
services.AddPerformanceOptimizedObjectPools();

// Provider-based features (geocoding, styling)
services.AddProviderBasedFeature<Provider, Registry, Factory, Coordinator, Service, Options>(
    configuration, "SectionName");

// Simple core features
services.AddSimpleCoreFeature<IService, Implementation>();

// HTTP client services
services.AddResilientHttpClientService<Client, Service>("client-name");

// Database-dependent services
services.AddDatabaseDependentServices(
    (typeof(IService), typeof(Implementation), ServiceLifetime.Scoped));
```

### 3. Validation Framework

#### `ValidationPatterns`
Consistent validation and configuration parsing:

```csharp
// Configuration with validation
services.AddValidatedConfiguration<Options, Validator>(configSection);

// Custom validation predicate
services.AddValidatedConfiguration<Options>(configSection, 
    options => options.IsValid(), "Custom validation failed");

// Configuration parsing helpers
var value = ConfigurationParsing.ParsePositiveIntOrDefault(config["Key"], defaultValue);
var limits = ConfigurationParsing.ParseConfigurationSection<ImportLimits>(
    section, defaultLimits, customParser);
```

#### `ConfigurationValidator<T>`
Base class for configuration validators:

```csharp
public class MyConfigValidator : ConfigurationValidator<MyConfig>
{
    protected override void PerformFeatureSpecificValidation(MyConfig options, List<string> errors)
    {
        ValidateRequired(options.RequiredProperty, nameof(options.RequiredProperty), errors);
        ValidateRange(options.NumericProperty, 1, 100, nameof(options.NumericProperty), errors);
        ValidateCollectionNotEmpty(options.Items, nameof(options.Items), errors);
        ValidateUri(options.Endpoint, nameof(options.Endpoint), errors);
        ValidateFilePath(options.Path, nameof(options.Path), errors, mustExist: true);
        ValidateConnectionString(options.ConnectionString, nameof(options.ConnectionString), errors);
    }
}
```

## Usage Examples

### 1. Simple Core Feature (Before/After)

**Before (3 separate files, ~10 lines each):**
```csharp
// AutoDocs/ServiceCollectionExtensions.cs
public static IServiceCollection AddAutoDocsCore(this IServiceCollection services)
{
    services.TryAddSingleton<IMetadataDocumentGenerator, MetadataDocumentGenerator>();
    return services;
}

// Import/ServiceCollectionExtensions.cs  
public static IServiceCollection AddImportSuggestionsCore(this IServiceCollection services)
{
    services.TryAddSingleton<IImportSchemaSuggestionService, ImportSchemaSuggestionService>();
    return services;
}

// Styling/ServiceCollectionExtensions.cs
public static IServiceCollection AddStyleSuggestionCore(this IServiceCollection services)
{
    services.TryAddScoped<IStyleSuggestionService, StyleSuggestionService>();
    return services;
}
```

**After (1 file, ~5 lines):**
```csharp
public static IServiceCollection AddSimpleCoreFeatures(this IServiceCollection services)
{
    return services
        .AddSimpleCoreFeature<IMetadataDocumentGenerator, MetadataDocumentGenerator>(ServiceLifetime.Singleton)
        .AddSimpleCoreFeature<IImportSchemaSuggestionService, ImportSchemaSuggestionService>(ServiceLifetime.Singleton)
        .AddSimpleCoreFeature<IStyleSuggestionService, StyleSuggestionService>();
}
```

### 2. Complex Feature with Configuration (Before/After)

**Before (~50 lines):**
```csharp
public static IServiceCollection AddGeocodingCore(this IServiceCollection services, IConfiguration configuration)
{
    // Configuration binding (8 lines)
    services.AddOptions<GeocodingConfiguration>()
        .Bind(configuration.GetSection(GeocodingConfiguration.SectionName))
        .ValidateOnStart();
    services.AddSingleton<IValidateOptions<GeocodingConfiguration>, GeocodingConfigurationValidator>();

    // Service registration (15 lines)  
    services.TryAddScoped<IGeocodeProviderRegistry, GeocodeProviderRegistry>();
    services.TryAddScoped<IGeocodeProviderFactory, GeocodeProviderFactory>();
    services.TryAddScoped<IGeocodeProviderCoordinator, GeocodeProviderCoordinator>();
    services.TryAddScoped<IGeocodeCoordinatorService, GeocodeCoordinatorService>();

    // Provider registration methods (25 lines)
    // ... complex provider registration logic
    
    return services;
}
```

**After (~10 lines):**
```csharp
public static IServiceCollection AddGeocodingCore(this IServiceCollection services, IConfiguration configuration)
{
    var configSection = configuration.GetSection(GeocodingConfiguration.SectionName);

    return services
        .AddValidatedConfiguration<GeocodingConfiguration, GeocodingConfigurationValidator>(configSection)
        .AddScopedService<IGeocodeProviderRegistry, GeocodeProviderRegistry>()
        .AddScopedService<IGeocodeProviderFactory, GeocodeProviderFactory>()
        .AddScopedService<IGeocodeProviderCoordinator, GeocodeProviderCoordinator>()
        .AddScopedService<IGeocodeCoordinatorService, GeocodeCoordinatorService>()
        .AddProviderRegistry<IGeocodeProvider, GeocodeProviderRegistrationOptions>();
}
```

### 3. PostgreSQL Feature Store (Before/After)

**Before (~30 lines):**
```csharp
public static IServiceCollection AddRefactoredFeatureStore(this IServiceCollection services, string? schemaName = null)
{
    var poolProvider = new DefaultObjectPoolProvider();

    // Object pools (8 lines)
    services.AddSingleton<ObjectPool<StringBuilder>>(_ =>
        poolProvider.Create(new Services.StringBuilderPooledObjectPolicy()));
    services.AddSingleton<ObjectPool<Dictionary<string, object?>>>(_ =>
        poolProvider.Create(new DictionaryPooledObjectPolicy()));

    // Core services (15 lines)
    services.AddSingleton<IGeometryProcessor>(_ => new GeometryProcessor());
    services.AddScoped<IFeatureCacheManager>(provider => /* complex factory */);
    services.AddScoped<IFeatureQueryBuilder>(provider => /* complex factory */);
    services.AddScoped<IFeatureDataAccess>(provider => /* complex factory */);

    // Segregated interfaces (7 lines)
    services.AddScoped<PostgresFeatureStoreRefactored>();
    services.AddScoped<IFeatureReader>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
    services.AddScoped<IFeatureWriter>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
    // ... more interfaces
    
    return services;
}
```

**After (~8 lines):**
```csharp
public static IServiceCollection AddRefactoredFeatureStoreConsolidated(this IServiceCollection services, string? schemaName = null)
{
    return services
        .AddPerformanceOptimizedObjectPools()
        .AddSingletonService<IGeometryProcessor, GeometryProcessor>()
        .AddSchemaBasedService<IFeatureCacheManager, FeatureCacheManager>(schemaName)
        .AddSchemaBasedService<IFeatureQueryBuilder, FeatureQueryBuilder>(schemaName)
        .AddSchemaBasedService<IFeatureDataAccess, FeatureDataAccess>(schemaName)
        .AddFeatureStoreServices<PostgresFeatureStoreRefactored>(schemaName,
            typeof(IFeatureReader), typeof(IFeatureWriter), typeof(ITileProvider),
            typeof(IRelationshipStore), typeof(IGeoJsonFeatureStore), typeof(IGeobufFeatureStore));
}
```

## Migration Guide

### 1. Identify Registration Pattern

Determine which pattern your services follow:

- **Simple Core**: Single service with interface/implementation
- **PostgreSQL Schema-based**: Services that accept schema name
- **Provider-based**: Plugin/provider pattern with registry
- **Feature Store**: Multiple segregated interfaces
- **Configuration-driven**: Services with complex configuration

### 2. Replace with Consolidated Pattern

Use the appropriate helper method from the framework:

```csharp
// OLD:
services.TryAddScoped<IMyService, MyService>();

// NEW:  
services.AddScopedService<IMyService, MyService>();

// OLD:
services.AddScoped<IMyStore>(provider => new PostgresMyStore(
    provider.GetRequiredService<IDatabaseConnectionProvider>(),
    provider.GetRequiredService<ILogger<PostgresMyStore>>(),
    schemaName));

// NEW:
services.AddSchemaBasedService<IMyStore, PostgresMyStore>(schemaName);
```

### 3. Update Configuration

Replace custom configuration binding with validated pattern:

```csharp
// OLD:
services.AddOptions<MyOptions>()
    .Bind(configuration.GetSection("MySection"))
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<MyOptions>, MyOptionsValidator>();

// NEW:
services.AddValidatedConfiguration<MyOptions, MyOptionsValidator>(
    configuration.GetSection("MySection"));
```

### 4. Test Migration

Ensure all services resolve correctly:

```csharp
[Fact]
public void MigratedServices_ResolveCorrectly()
{
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().Build();
    
    // Use new consolidated registration
    services.AddMyFeatureConsolidated(configuration);
    
    var serviceProvider = services.BuildServiceProvider();
    
    // Verify all expected services resolve
    Assert.NotNull(serviceProvider.GetService<IMyService>());
    Assert.NotNull(serviceProvider.GetService<IMyStore>());
}
```

## Benefits

1. **Reduced Duplication**: 85% reduction in repetitive registration code
2. **Consistency**: Standardized patterns across all features
3. **Maintainability**: Single framework to update for registration changes
4. **Type Safety**: Compile-time checking of registration patterns
5. **Testability**: Framework components are thoroughly tested
6. **Documentation**: Self-documenting through method names
7. **Validation**: Consistent configuration validation patterns
8. **Performance**: Object pooling and optimization patterns built-in

## Best Practices

1. **Use Appropriate Lifetime**: Choose correct ServiceLifetime for your services
2. **Schema Consistency**: Always pass schema name for database services
3. **Configuration Validation**: Always validate configuration options
4. **Provider Patterns**: Use registry pattern for pluggable services
5. **Read-only Fallbacks**: Implement read-only services for unsupported operations
6. **Test Coverage**: Write tests for consolidated registrations
7. **Documentation**: Document custom patterns in your consolidated extensions

## Future Enhancements

1. **Auto-discovery**: Automatically discover services based on interfaces
2. **Configuration Schema**: JSON schema validation for configuration
3. **Health Checks**: Built-in health check registration
4. **Metrics**: Automatic registration metrics collection
5. **Hot Reload**: Support for configuration hot-reload
6. **Code Generation**: Generate registration code from attributes