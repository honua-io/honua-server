// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public sealed class ConfigurationValidationResult
{
    public ConfigurationValidationResult(IEnumerable<ValidationResult> errors, bool isDevelopment)
    {
        Errors = errors?.ToArray() ?? [];
        Warnings = Array.Empty<ValidationResult>();
        IsDevelopment = isDevelopment;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<ValidationResult> Errors { get; }

    public IReadOnlyList<ValidationResult> Warnings { get; }

    public bool IsDevelopment { get; }
}

public sealed class OptionsValidationResult
{
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

    public string SectionName { get; }

    public string OptionsTypeName { get; }

    public ConfigurationValidationResult Result { get; }

    public bool IsRequired { get; }

    public bool IsValid => Result.IsValid;

    public IReadOnlyList<ValidationResult> Errors => Result.Errors;

    public IReadOnlyList<ValidationResult> Warnings => Result.Warnings;
}

public sealed class ConfigurationValidationSummary
{
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

    public IReadOnlyList<OptionsValidationResult> Results { get; }

    public bool IsValid => TotalErrors == 0;

    public int TotalErrors { get; }

    public int TotalWarnings { get; }

    public IReadOnlyList<string> AllErrors { get; }

    public IReadOnlyList<string> AllWarnings { get; }
}

public sealed record ConfigurationOptionsMetadata(
    string SectionName,
    [param: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type OptionsType,
    IReadOnlyList<ConfigurationPropertyMetadata> Properties,
    bool IsRequired,
    string? Description);

public sealed record ConfigurationPropertyMetadata(
    string Name,
    Type PropertyType,
    object? DefaultValue,
    bool IsRequired,
    string? Description,
    IReadOnlyList<ValidationAttribute> ValidationAttributes);
