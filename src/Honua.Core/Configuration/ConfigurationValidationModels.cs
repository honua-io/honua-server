// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Represents the validation result for a bound options instance.
/// </summary>
public sealed class ConfigurationValidationResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationValidationResult"/> class.
    /// </summary>
    /// <param name="errors">The validation errors.</param>
    /// <param name="isDevelopment">Whether the validation ran in development mode.</param>
    public ConfigurationValidationResult(IEnumerable<ValidationResult> errors, bool isDevelopment)
    {
        Errors = errors?.ToArray() ?? [];
        Warnings = Array.Empty<ValidationResult>();
        IsDevelopment = isDevelopment;
    }

    /// <summary>
    /// Gets a value indicating whether the validation passed.
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// Gets the validation errors.
    /// </summary>
    public IReadOnlyList<ValidationResult> Errors { get; }

    /// <summary>
    /// Gets the validation warnings.
    /// </summary>
    public IReadOnlyList<ValidationResult> Warnings { get; }

    /// <summary>
    /// Gets a value indicating whether validation ran in development mode.
    /// </summary>
    public bool IsDevelopment { get; }
}

/// <summary>
/// Represents the validation result for a single options type.
/// </summary>
public sealed class OptionsValidationResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OptionsValidationResult"/> class.
    /// </summary>
    /// <param name="sectionName">The configuration section name.</param>
    /// <param name="optionsTypeName">The options type name.</param>
    /// <param name="result">The validation result.</param>
    /// <param name="isRequired">Whether the configuration section is required.</param>
    public OptionsValidationResult(
        string sectionName,
        string optionsTypeName,
        ConfigurationValidationResult result,
        bool isRequired)
    {
        SectionName = sectionName;
        OptionsTypeName = optionsTypeName;
        Result = result;
        IsRequired = isRequired;
    }

    /// <summary>
    /// Gets the configuration section name.
    /// </summary>
    public string SectionName { get; }

    /// <summary>
    /// Gets the options type name.
    /// </summary>
    public string OptionsTypeName { get; }

    /// <summary>
    /// Gets the validation result.
    /// </summary>
    public ConfigurationValidationResult Result { get; }

    /// <summary>
    /// Gets a value indicating whether the configuration section is required.
    /// </summary>
    public bool IsRequired { get; }

    /// <summary>
    /// Gets a value indicating whether the validation passed.
    /// </summary>
    public bool IsValid => Result.IsValid;

    /// <summary>
    /// Gets the validation errors.
    /// </summary>
    public IReadOnlyList<ValidationResult> Errors => Result.Errors;

    /// <summary>
    /// Gets the validation warnings.
    /// </summary>
    public IReadOnlyList<ValidationResult> Warnings => Result.Warnings;
}

/// <summary>
/// Aggregates validation results across all registered configuration options.
/// </summary>
public sealed class ConfigurationValidationSummary
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationValidationSummary"/> class.
    /// </summary>
    /// <param name="results">The per-options validation results.</param>
    public ConfigurationValidationSummary(IEnumerable<OptionsValidationResult> results)
    {
        Results = results?.ToArray() ?? [];
        TotalErrors = Results.Sum(static result => result.Errors.Count);
        TotalWarnings = Results.Sum(static result => result.Warnings.Count);
        AllErrors = Results
            .SelectMany(static result => result.Errors.Select(static error => error.ErrorMessage ?? "Validation failed"))
            .ToArray();
        AllWarnings = Results
            .SelectMany(static result => result.Warnings.Select(static warning => warning.ErrorMessage ?? "Validation warning"))
            .ToArray();
    }

    /// <summary>
    /// Gets the per-options validation results.
    /// </summary>
    public IReadOnlyList<OptionsValidationResult> Results { get; }

    /// <summary>
    /// Gets a value indicating whether all validation results passed.
    /// </summary>
    public bool IsValid => TotalErrors == 0;

    /// <summary>
    /// Gets the total number of validation errors.
    /// </summary>
    public int TotalErrors { get; }

    /// <summary>
    /// Gets the total number of validation warnings.
    /// </summary>
    public int TotalWarnings { get; }

    /// <summary>
    /// Gets the flattened list of validation error messages.
    /// </summary>
    public IReadOnlyList<string> AllErrors { get; }

    /// <summary>
    /// Gets the flattened list of validation warning messages.
    /// </summary>
    public IReadOnlyList<string> AllWarnings { get; }
}

/// <summary>
/// Describes a registered options type and its properties.
/// </summary>
/// <param name="SectionName">The configuration section name.</param>
/// <param name="OptionsType">The options CLR type.</param>
/// <param name="Properties">The discovered property metadata.</param>
/// <param name="IsRequired">Whether the section is required.</param>
/// <param name="Description">The human-readable description.</param>
public sealed record ConfigurationOptionsMetadata(
    string SectionName,
    Type OptionsType,
    IReadOnlyList<ConfigurationPropertyMetadata> Properties,
    bool IsRequired,
    string? Description);

/// <summary>
/// Describes a single configuration property.
/// </summary>
/// <param name="Name">The property name.</param>
/// <param name="PropertyType">The property CLR type.</param>
/// <param name="DefaultValue">The default property value.</param>
/// <param name="IsRequired">Whether the property is required.</param>
/// <param name="Description">The human-readable description.</param>
/// <param name="ValidationAttributes">The attached validation attributes.</param>
public sealed record ConfigurationPropertyMetadata(
    string Name,
    Type PropertyType,
    object? DefaultValue,
    bool IsRequired,
    string? Description,
    IReadOnlyList<ValidationAttribute> ValidationAttributes);
