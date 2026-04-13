// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Honua.Core.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Configuration;

/// <summary>
/// Configuration validator that uses data annotations and provides comprehensive validation reporting.
/// </summary>
internal sealed class ConfigurationValidator : IConfigurationValidator, IConfigurationDiscovery
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationValidator> _logger;
    private readonly ConcurrentDictionary<Type, ConfigurationOptionsMetadata> _registeredOptions = new();

    /// <summary>
    /// Initializes a new instance of the ConfigurationValidator.
    /// </summary>
    public ConfigurationValidator(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<ConfigurationValidator> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validates a configuration options instance using data annotations.
    /// </summary>
    public ConfigurationValidationResult ValidateOptions<TOptions>(TOptions options, string sectionName, bool isDevelopment = false)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var context = new ValidationContext(options)
        {
            DisplayName = typeof(TOptions).Name
        };
        context.Items["IsDevelopment"] = isDevelopment;
        context.Items["SectionName"] = sectionName;

        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(options, context, validationResults, validateAllProperties: true);

        // Add custom validation for configuration-specific attributes
        ValidateConfigurationAttributes(options, context, validationResults, sectionName);

        return new ConfigurationValidationResult(validationResults, isDevelopment);
    }

    /// <summary>
    /// Validates all registered configuration options at startup.
    /// </summary>
    public async Task<ConfigurationValidationSummary> ValidateAllAsync(bool isDevelopment = false, bool isTest = false)
    {
        var results = new List<OptionsValidationResult>();
        var validationTasks = new List<Task<OptionsValidationResult>>();

        foreach (var (optionsType, metadata) in _registeredOptions)
        {
            validationTasks.Add(ValidateOptionsTypeAsync(optionsType, metadata, isDevelopment, isTest));
        }

        if (validationTasks.Count > 0)
        {
            var completedResults = await Task.WhenAll(validationTasks).ConfigureAwait(false);
            results.AddRange(completedResults);
        }

        var summary = new ConfigurationValidationSummary(results);

        // Log summary
        if (summary.IsValid)
        {
            _logger.LogInformation("Configuration validation completed successfully. Validated {Count} sections",
                results.Count);
        }
        else
        {
            _logger.LogError(
                "Configuration validation failed with {ErrorCount} errors and {WarningCount} warnings across {SectionCount} sections",
                summary.TotalErrors, summary.TotalWarnings, results.Count);

            foreach (var error in summary.AllErrors)
            {
                _logger.LogError("Configuration error: {Error}", error);
            }
        }

        if (summary.TotalWarnings > 0)
        {
            foreach (var warning in summary.AllWarnings)
            {
                _logger.LogWarning("Configuration warning: {Warning}", warning);
            }
        }

        return summary;
    }

    /// <summary>
    /// Registers a configuration options type for validation.
    /// </summary>
    public void RegisterOptionsType<TOptions>(string sectionName, bool isRequired = true) where TOptions : class
    {
        var metadata = CreateOptionsMetadata<TOptions>(sectionName, isRequired);
        _registeredOptions[typeof(TOptions)] = metadata;

        _logger.LogDebug("Registered configuration options type {OptionsType} for section {SectionName}",
            typeof(TOptions).Name, sectionName);
    }

    /// <summary>
    /// Gets all registered configuration options types.
    /// </summary>
    public IEnumerable<ConfigurationOptionsMetadata> GetAllOptions() =>
        _registeredOptions.Values.ToList();

    /// <summary>
    /// Gets configuration metadata for a specific options type.
    /// </summary>
    public ConfigurationOptionsMetadata GetOptionsMetadata<TOptions>() where TOptions : class
    {
        if (_registeredOptions.TryGetValue(typeof(TOptions), out var metadata))
        {
            return metadata;
        }

        throw new InvalidOperationException($"Options type {typeof(TOptions).Name} has not been registered for validation");
    }

    /// <summary>
    /// Validates a specific options type asynchronously.
    /// </summary>
    private async Task<OptionsValidationResult> ValidateOptionsTypeAsync(
        Type optionsType,
        ConfigurationOptionsMetadata metadata,
        bool isDevelopment,
        bool isTest)
    {
        try
        {
            // Get the configured options instance
            var optionsInstance = GetConfiguredOptionsInstance(optionsType, metadata.SectionName);

            if (optionsInstance == null)
            {
                var error = new ValidationResult($"Configuration section '{metadata.SectionName}' could not be bound to {optionsType.Name}");
                var result = new ConfigurationValidationResult(new[] { error }, isDevelopment);
                return new OptionsValidationResult(metadata.SectionName, optionsType.Name, result, metadata.IsRequired);
            }

            // Validate using reflection to call generic method
            var method = GetType().GetMethod(nameof(ValidateOptions), BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(optionsType);

            var validationResult = (ConfigurationValidationResult)method.Invoke(this, new[] { optionsInstance, metadata.SectionName, isDevelopment })!;

            return new OptionsValidationResult(metadata.SectionName, optionsType.Name, validationResult, metadata.IsRequired);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate configuration section {SectionName} of type {OptionsType}",
                metadata.SectionName, optionsType.Name);

            var error = new ValidationResult($"Validation failed for {metadata.SectionName}: {ex.Message}");
            var result = new ConfigurationValidationResult(new[] { error }, isDevelopment);
            return new OptionsValidationResult(metadata.SectionName, optionsType.Name, result, metadata.IsRequired);
        }
    }

    /// <summary>
    /// Gets a configured options instance from the service provider or configuration.
    /// </summary>
    private object? GetConfiguredOptionsInstance(Type optionsType, string sectionName)
    {
        try
        {
            // First try to get from DI container (if already configured)
            var optionsAccessorType = typeof(IOptions<>).MakeGenericType(optionsType);
            var optionsAccessor = _serviceProvider.GetService(optionsAccessorType);

            if (optionsAccessor != null)
            {
                var valueProperty = optionsAccessorType.GetProperty("Value");
                return valueProperty?.GetValue(optionsAccessor);
            }
        }
        catch
        {
            // Fall back to manual binding if DI resolution fails
        }

        // Fallback: Create instance and bind manually
        try
        {
            var instance = Activator.CreateInstance(optionsType);
            // Use generic method overload to avoid source generator issues
            var method = typeof(ConfigurationBinder).GetMethod(nameof(ConfigurationBinder.Bind), new[] { typeof(IConfiguration), typeof(object) });
            method?.Invoke(null, new[] { _configuration.GetSection(sectionName), instance });
            return instance;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create and bind configuration instance for {OptionsType}", optionsType.Name);
            return null;
        }
    }

    /// <summary>
    /// Validates configuration-specific attributes that require additional context.
    /// </summary>
    private void ValidateConfigurationAttributes(
        object options,
        ValidationContext context,
        List<ValidationResult> validationResults,
        string sectionName)
    {
        var properties = options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var configAttributes = property.GetCustomAttributes<ConfigurationValidationAttribute>().ToList();

            foreach (var attribute in configAttributes)
            {
                // Set configuration path for better error messages
                attribute.ConfigurationPath ??= sectionName;

                var propertyContext = new ValidationContext(options, context, context.Items)
                {
                    MemberName = property.Name,
                    DisplayName = property.Name
                };

                var value = property.GetValue(options);
                var result = attribute.IsValid(value, propertyContext);

                if (result != null && result != ValidationResult.Success)
                {
                    validationResults.Add(result);
                }
            }
        }
    }

    /// <summary>
    /// Creates metadata for a configuration options type.
    /// </summary>
    private static ConfigurationOptionsMetadata CreateOptionsMetadata<TOptions>(string sectionName, bool isRequired)
        where TOptions : class
    {
        var type = typeof(TOptions);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var propertyMetadata = properties.Select(p => CreatePropertyMetadata(p)).ToList();

        // Try to get description from type-level attributes or XML documentation
        var description = GetTypeDescription(type);

        return new ConfigurationOptionsMetadata(sectionName, type, propertyMetadata, isRequired, description);
    }

    /// <summary>
    /// Creates metadata for a configuration property.
    /// </summary>
    private static ConfigurationPropertyMetadata CreatePropertyMetadata(PropertyInfo property)
    {
        var defaultValue = GetDefaultValue(property);
        var isRequired = property.GetCustomAttributes<RequiredAttribute>().Any() ||
                        property.GetCustomAttributes<RequiredConfigurationAttribute>().Any();
        var description = GetPropertyDescription(property);
        var validationAttributes = property.GetCustomAttributes<ValidationAttribute>().ToList();

        return new ConfigurationPropertyMetadata(
            property.Name,
            property.PropertyType,
            defaultValue,
            isRequired,
            description,
            validationAttributes);
    }

    /// <summary>
    /// Gets the default value for a property by creating a default instance.
    /// </summary>
    private static object? GetDefaultValue(PropertyInfo property)
    {
        try
        {
            var instance = Activator.CreateInstance(property.DeclaringType!);
            return property.GetValue(instance);
        }
        catch
        {
            return property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
        }
    }

    /// <summary>
    /// Gets description for a type from attributes or documentation.
    /// </summary>
    private static string? GetTypeDescription(Type type)
    {
        // You could extend this to read XML documentation comments
        return type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
    }

    /// <summary>
    /// Gets description for a property from attributes or documentation.
    /// </summary>
    private static string? GetPropertyDescription(PropertyInfo property)
    {
        // You could extend this to read XML documentation comments
        return property.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
    }
}