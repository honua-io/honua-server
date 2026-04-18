// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Configuration;
using Honua.Core.Configuration.Validation;
using Microsoft.Extensions.Configuration;
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
    private readonly ConcurrentDictionary<Type, IConfigurationOptionsRegistration> _registeredOptions = new();

    /// <summary>
    /// Initializes a new instance of the ConfigurationValidator.
    /// </summary>
    public ConfigurationValidator(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<ConfigurationValidator> logger,
        IEnumerable<IConfigurationOptionsRegistration> registrations)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var registration in registrations)
        {
            _registeredOptions[registration.Metadata.OptionsType] = registration;
        }
    }

    /// <inheritdoc />
    public string ConfigurationSection => string.Empty;

    /// <inheritdoc />
    public IEnumerable<string> ValidateConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ValidateAllAsync().GetAwaiter().GetResult().AllErrors;
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

        return ValidateOptionsInstance(options, sectionName, isDevelopment, ConfigurationOptionsMetadataFactory.CreatePropertyAccessors<TOptions>());
    }

    /// <summary>
    /// Validates all registered configuration options at startup.
    /// </summary>
    public async Task<ConfigurationValidationSummary> ValidateAllAsync(bool isDevelopment = false, bool isTest = false)
    {
        var results = new List<OptionsValidationResult>();
        var validationTasks = new List<Task<OptionsValidationResult>>();

        foreach (var registration in _registeredOptions.Values)
        {
            validationTasks.Add(ValidateOptionsTypeAsync(registration, isDevelopment, isTest));
        }

        if (validationTasks.Count > 0)
        {
            var completedResults = await Task.WhenAll(validationTasks).ConfigureAwait(false);
            results.AddRange(completedResults);
        }

        var summary = new ConfigurationValidationSummary(results);

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
        var registration = new ManualConfigurationOptionsRegistration<TOptions>(_configuration, sectionName, isRequired);
        _registeredOptions[typeof(TOptions)] = registration;

        _logger.LogDebug("Registered configuration options type {OptionsType} for section {SectionName}",
            typeof(TOptions).Name, sectionName);
    }

    /// <summary>
    /// Gets all registered configuration options types.
    /// </summary>
    public IEnumerable<ConfigurationOptionsMetadata> GetAllOptions() =>
        _registeredOptions.Values.Select(static registration => registration.Metadata).ToList();

    /// <summary>
    /// Gets configuration metadata for a specific options type.
    /// </summary>
    public ConfigurationOptionsMetadata GetOptionsMetadata<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>()
        where TOptions : class
    {
        if (_registeredOptions.TryGetValue(typeof(TOptions), out var metadata))
        {
            return metadata.Metadata;
        }

        throw new InvalidOperationException($"Options type {typeof(TOptions).Name} has not been registered for validation");
    }

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

            if (optionsInstance == null)
            {
                var error = new ValidationResult($"Configuration section '{metadata.SectionName}' could not be bound to {optionsType.Name}");
                var result = new ConfigurationValidationResult(new[] { error }, isDevelopment);
                return new OptionsValidationResult(metadata.SectionName, optionsType.Name, result, metadata.IsRequired);
            }

            var validationResult = ValidateOptionsInstance(
                optionsInstance,
                metadata.SectionName,
                isDevelopment,
                registration.PropertyAccessors);

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

    private object? GetConfiguredOptionsInstance(IConfigurationOptionsRegistration registration)
    {
        try
        {
            return registration.GetConfiguredOptions(_serviceProvider);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve configuration instance for {OptionsType}", registration.Metadata.OptionsType.Name);
            return null;
        }
    }

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
}
