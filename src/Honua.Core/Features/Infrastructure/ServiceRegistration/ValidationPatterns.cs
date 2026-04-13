// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

/// <summary>
/// Common validation patterns and configuration parsing helpers.
/// </summary>
public static class ValidationPatterns
{
    /// <summary>
    /// Register configuration options with fluent validation and startup validation.
    /// </summary>
    public static IServiceCollection AddValidatedConfiguration<TOptions, TValidator>(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>()
            .Bind(configurationSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();

        return services;
    }

    /// <summary>
    /// Register configuration options with data annotations validation only.
    /// </summary>
    public static IServiceCollection AddValidatedConfiguration<TOptions>(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configurationSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Register configuration options with custom validation predicate.
    /// </summary>
    public static IServiceCollection AddValidatedConfiguration<TOptions>(
        this IServiceCollection services,
        IConfigurationSection configurationSection,
        Func<TOptions, bool> validator,
        string failureMessage)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configurationSection)
            .Validate(validator, failureMessage)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Common configuration parsing methods to eliminate duplication.
    /// </summary>
    public static class ConfigurationParsing
    {
        public static int ParsePositiveIntOrDefault(string? value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : defaultValue;
        }

        public static int ParseNonNegativeIntOrDefault(string? value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
                ? parsed
                : defaultValue;
        }

        public static long ParsePositiveLongOrDefault(string? value, long defaultValue)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : defaultValue;
        }

        public static double ParsePositiveDoubleOrDefault(string? value, double defaultValue)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : defaultValue;
        }

        public static bool ParseBoolOrDefault(string? value, bool defaultValue)
        {
            return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        public static string ParseStringOrDefault(string? value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        }

        public static TimeSpan ParseTimeSpanOrDefault(string? value, TimeSpan defaultValue)
        {
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        /// <summary>
        /// Parse a configuration section into a strongly-typed object with validation.
        /// </summary>
        public static TOptions ParseConfigurationSection<TOptions>(
            IConfigurationSection section,
            TOptions defaultOptions,
            Action<TOptions, IConfigurationSection>? customParser = null)
            where TOptions : class, new()
        {
            if (!section.Exists())
            {
                return defaultOptions;
            }

            var options = new TOptions();

            // Bind standard properties
            section.Bind(options);

            // Apply custom parsing if provided
            customParser?.Invoke(options, section);

            // Validate using data annotations
            var validationResults = new List<ValidationResult>();
            var validationContext = new ValidationContext(options);

            if (!Validator.TryValidateObject(options, validationContext, validationResults, true))
            {
                var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
                throw new InvalidOperationException($"Configuration validation failed for {typeof(TOptions).Name}: {errors}");
            }

            return options;
        }
    }
}

/// <summary>
/// Base class for configuration validators with common patterns.
/// </summary>
public abstract class ConfigurationValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        // Perform common validation
        PerformCommonValidation(options, errors);

        // Perform feature-specific validation
        PerformFeatureSpecificValidation(options, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// Perform validation common across all features.
    /// Override to customize common validation.
    /// </summary>
    protected virtual void PerformCommonValidation(TOptions options, List<string> errors)
    {
        // Validate using data annotations
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(options);

        if (!Validator.TryValidateObject(options, validationContext, validationResults, true))
        {
            errors.AddRange(validationResults.Select(r => r.ErrorMessage ?? "Unknown validation error"));
        }
    }

    /// <summary>
    /// Perform feature-specific validation. Must be implemented by derived classes.
    /// </summary>
    protected abstract void PerformFeatureSpecificValidation(TOptions options, List<string> errors);

    /// <summary>
    /// Helper to validate that a string property is not null or whitespace.
    /// </summary>
    protected static void ValidateRequired(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} is required and cannot be empty or whitespace.");
        }
    }

    /// <summary>
    /// Helper to validate that a numeric property is within a range.
    /// </summary>
    protected static void ValidateRange<T>(T value, T min, T max, string propertyName, List<string> errors)
        where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
        {
            errors.Add($"{propertyName} must be between {min} and {max}, but was {value}.");
        }
    }

    /// <summary>
    /// Helper to validate that a collection is not null or empty.
    /// </summary>
    protected static void ValidateCollectionNotEmpty<T>(IEnumerable<T>? collection, string propertyName, List<string> errors)
    {
        if (collection == null || !collection.Any())
        {
            errors.Add($"{propertyName} cannot be null or empty.");
        }
    }

    /// <summary>
    /// Helper to validate that a URI is valid.
    /// </summary>
    protected static void ValidateUri(string? uriString, string propertyName, List<string> errors, UriKind uriKind = UriKind.Absolute)
    {
        if (!string.IsNullOrWhiteSpace(uriString) && !Uri.TryCreate(uriString, uriKind, out _))
        {
            errors.Add($"{propertyName} must be a valid URI, but was '{uriString}'.");
        }
    }

    /// <summary>
    /// Helper to validate that a file path exists.
    /// </summary>
    protected static void ValidateFilePath(string? filePath, string propertyName, List<string> errors, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                errors.Add($"{propertyName} path '{filePath}' does not exist.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{propertyName} path '{filePath}' is invalid: {ex.Message}");
        }
    }

    /// <summary>
    /// Helper to validate database connection string format.
    /// </summary>
    protected static void ValidateConnectionString(string? connectionString, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        // Basic validation - must contain key-value pairs separated by semicolons
        if (!connectionString.Contains('=') && !connectionString.Contains(';'))
        {
            errors.Add($"{propertyName} does not appear to be a valid connection string format.");
        }
    }
}