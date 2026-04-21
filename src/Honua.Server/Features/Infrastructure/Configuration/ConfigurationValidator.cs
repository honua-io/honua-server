<<<<<<< HEAD
// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// This validator builds reflective configuration metadata for admin tooling and startup validation.
// The code is intentionally dynamic and localized here rather than in request-path components.
#pragma warning disable IL2067, IL2071, IL2072, IL2090

using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Honua.Core.Configuration;
using Honua.Core.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
=======
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Configuration;
using Honua.Core.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
>>>>>>> origin/trunk

namespace Honua.Server.Features.Infrastructure.Configuration;

/// <summary>
/// Configuration validator that uses data annotations and provides comprehensive validation reporting.
/// </summary>
internal sealed class ConfigurationValidator : IConfigurationValidator, IConfigurationDiscovery
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationValidator> _logger;
<<<<<<< HEAD
    private readonly ConcurrentDictionary<Type, ConfigurationOptionsMetadata> _registeredOptions = new();
=======
    private readonly ConcurrentDictionary<Type, IConfigurationOptionsRegistration> _registeredOptions = new();
>>>>>>> origin/trunk

    /// <summary>
    /// Initializes a new instance of the ConfigurationValidator.
    /// </summary>
    public ConfigurationValidator(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
<<<<<<< HEAD
        ILogger<ConfigurationValidator> logger)
=======
        ILogger<ConfigurationValidator> logger,
        IEnumerable<IConfigurationOptionsRegistration> registrations)
>>>>>>> origin/trunk
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
<<<<<<< HEAD
=======

        foreach (var registration in registrations)
        {
            _registeredOptions[registration.Metadata.OptionsType] = registration;
        }
>>>>>>> origin/trunk
    }

    /// <inheritdoc />
    public string ConfigurationSection => string.Empty;

    /// <inheritdoc />
    public IEnumerable<string> ValidateConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
<<<<<<< HEAD
        // Use Task.Run with ConfigureAwait(false) to safely validate in sync context
        // This is necessary because IConfigurationValidator is inherently synchronous
        return Task.Run(async () =>
            (await ValidateAllAsync().ConfigureAwait(false)).AllErrors).ConfigureAwait(false).GetAwaiter().GetResult();
=======
        return ValidateAllAsync().GetAwaiter().GetResult().AllErrors;
>>>>>>> origin/trunk
    }

    /// <summary>
    /// Validates a configuration options instance using data annotations.
    /// </summary>
    public ConfigurationValidationResult ValidateOptions<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        TOptions options,
        string sectionName,
        bool isDevelopment = false)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

<<<<<<< HEAD
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
=======
        return ValidateOptionsInstance(options, sectionName, isDevelopment, ConfigurationOptionsMetadataFactory.CreatePropertyAccessors<TOptions>());
>>>>>>> origin/trunk
    }

    /// <summary>
    /// Validates all registered configuration options at startup.
    /// </summary>
    public async Task<ConfigurationValidationSummary> ValidateAllAsync(bool isDevelopment = false, bool isTest = false)
    {
        var results = new List<OptionsValidationResult>();
        var validationTasks = new List<Task<OptionsValidationResult>>();

<<<<<<< HEAD
        foreach (var metadata in _registeredOptions.Values)
        {
            validationTasks.Add(ValidateOptionsTypeAsync(metadata.OptionsType, metadata, isDevelopment, isTest));
=======
        foreach (var registration in _registeredOptions.Values)
        {
            validationTasks.Add(ValidateOptionsTypeAsync(registration, isDevelopment, isTest));
>>>>>>> origin/trunk
        }

        if (validationTasks.Count > 0)
        {
            var completedResults = await Task.WhenAll(validationTasks).ConfigureAwait(false);
            results.AddRange(completedResults);
        }

        var summary = new ConfigurationValidationSummary(results);

<<<<<<< HEAD
        // Log summary
=======
>>>>>>> origin/trunk
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
    public void RegisterOptionsType<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        string sectionName,
        bool isRequired = true)
        where TOptions : class
    {
<<<<<<< HEAD
        var metadata = CreateOptionsMetadata<TOptions>(sectionName, isRequired);
        _registeredOptions[typeof(TOptions)] = metadata;
=======
        var registration = new ManualConfigurationOptionsRegistration<TOptions>(_configuration, sectionName, isRequired);
        _registeredOptions[typeof(TOptions)] = registration;
>>>>>>> origin/trunk

        _logger.LogDebug("Registered configuration options type {OptionsType} for section {SectionName}",
            typeof(TOptions).Name, sectionName);
    }

    /// <summary>
    /// Gets all registered configuration options types.
    /// </summary>
    public IEnumerable<ConfigurationOptionsMetadata> GetAllOptions() =>
<<<<<<< HEAD
        _registeredOptions.Values.ToList();
=======
        _registeredOptions.Values.Select(static registration => registration.Metadata).ToList();
>>>>>>> origin/trunk

    /// <summary>
    /// Gets configuration metadata for a specific options type.
    /// </summary>
    public ConfigurationOptionsMetadata GetOptionsMetadata<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>()
        where TOptions : class
    {
        if (_registeredOptions.TryGetValue(typeof(TOptions), out var metadata))
        {
<<<<<<< HEAD
            return metadata;
=======
            return metadata.Metadata;
>>>>>>> origin/trunk
        }

        throw new InvalidOperationException($"Options type {typeof(TOptions).Name} has not been registered for validation");
    }

<<<<<<< HEAD
    /// <summary>
    /// Validates a specific options type asynchronously.
    /// </summary>
    private async Task<OptionsValidationResult> ValidateOptionsTypeAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type optionsType,
        ConfigurationOptionsMetadata metadata,
        bool isDevelopment,
        bool isTest)
    {
        try
        {
            // Get the configured options instance
            var optionsInstance = GetConfiguredOptionsInstance(optionsType, metadata.SectionName);
=======
    private async Task<OptionsValidationResult> ValidateOptionsTypeAsync(
        IConfigurationOptionsRegistration registration,
        bool isDevelopment,
        bool isTest)
    {
        _ = isTest;

        var metadata = registration.Metadata;
        var optionsType = metadata.OptionsType;

        try
        {
            var optionsInstance = GetConfiguredOptionsInstance(registration);
>>>>>>> origin/trunk

            if (optionsInstance == null)
            {
                var error = new ValidationResult($"Configuration section '{metadata.SectionName}' could not be bound to {optionsType.Name}");
                var result = new ConfigurationValidationResult(new[] { error }, isDevelopment);
                return new OptionsValidationResult(metadata.SectionName, optionsType.Name, result, metadata.IsRequired);
            }

<<<<<<< HEAD
            // Validate using reflection to call generic method
            var method = GetType().GetMethod(nameof(ValidateOptions), BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(optionsType);

            var validationResult = (ConfigurationValidationResult)method.Invoke(this, new[] { optionsInstance, metadata.SectionName, isDevelopment })!;
=======
            var validationResult = ValidateOptionsInstance(
                optionsInstance,
                metadata.SectionName,
                isDevelopment,
                registration.PropertyAccessors);
>>>>>>> origin/trunk

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

<<<<<<< HEAD
    /// <summary>
    /// Gets a configured options instance from the service provider or configuration.
    /// </summary>
    private object? GetConfiguredOptionsInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type optionsType,
        string sectionName)
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
=======
    private object? GetConfiguredOptionsInstance(IConfigurationOptionsRegistration registration)
    {
        try
        {
            return registration.GetConfiguredOptions(_serviceProvider);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve configuration instance for {OptionsType}", registration.Metadata.OptionsType.Name);
>>>>>>> origin/trunk
            return null;
        }
    }

<<<<<<< HEAD
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
=======
    private static ConfigurationValidationResult ValidateOptionsInstance(
        object options,
        string sectionName,
        bool isDevelopment,
        IReadOnlyList<ConfigurationPropertyAccessor> propertyAccessors)
    {
        var context = new ValidationContext(options)
        {
            DisplayName = options.GetType().Name
        };
        context.Items["IsDevelopment"] = isDevelopment;
        context.Items["SectionName"] = sectionName;

        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(options, context, validationResults, validateAllProperties: true);

        ValidateConfigurationAttributes(options, context, validationResults, sectionName, propertyAccessors);

        return new ConfigurationValidationResult(validationResults, isDevelopment);
    }

    private static void ValidateConfigurationAttributes(
        object options,
        ValidationContext context,
        List<ValidationResult> validationResults,
        string sectionName,
        IReadOnlyList<ConfigurationPropertyAccessor> propertyAccessors)
    {
        foreach (var property in propertyAccessors)
        {
            foreach (var attribute in property.ValidationAttributes)
            {
>>>>>>> origin/trunk
                attribute.ConfigurationPath ??= sectionName;

                var propertyContext = new ValidationContext(options, context, context.Items)
                {
                    MemberName = property.Name,
                    DisplayName = property.Name
                };

                var value = property.GetValue(options);
                var result = attribute.GetValidationResult(value, propertyContext);

                if (result != null && result != ValidationResult.Success)
                {
                    validationResults.Add(result);
                }
            }
        }
    }
<<<<<<< HEAD

    /// <summary>
    /// Creates metadata for a configuration options type.
    /// </summary>
    private static ConfigurationOptionsMetadata CreateOptionsMetadata<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        string sectionName,
        bool isRequired)
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
        return property.GetCustomAttribute<DefaultValueAttribute>()?.Value;
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

#pragma warning restore IL2067, IL2071, IL2072, IL2090
=======
}
>>>>>>> origin/trunk
