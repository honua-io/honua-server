// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

using Honua.Core.Features.FeatureStore.Domain;

namespace HonuaFieldApp.Services;

/// <summary>
/// Interface for form data collection services.
/// This is a placeholder implementation for the app template.
/// In a real application, implement dynamic form generation and data validation.
/// </summary>
public interface IFormDataService
{
    /// <summary>
    /// Load form definition for the specified layer.
    /// </summary>
    Task<FormDefinition> GetFormDefinitionAsync(string serviceId, int layerId);

    /// <summary>
    /// Validate form data against the form definition.
    /// </summary>
    Task<ValidationResult> ValidateFormDataAsync(FormDefinition form, Dictionary<string, object> data);

    /// <summary>
    /// Create a feature from form data.
    /// </summary>
    Task<Feature> CreateFeatureFromFormAsync(FormDefinition form, Dictionary<string, object> data, byte[]? geometry = null);

    /// <summary>
    /// Save form as draft for later completion.
    /// </summary>
    Task SaveDraftAsync(string draftId, Dictionary<string, object> data);

    /// <summary>
    /// Load draft form data.
    /// </summary>
    Task<Dictionary<string, object>?> LoadDraftAsync(string draftId);
}

/// <summary>
/// Represents a form definition for data collection.
/// </summary>
public class FormDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<FormField> Fields { get; set; } = new();
}

/// <summary>
/// Represents a field in a form definition.
/// </summary>
public class FormField
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public List<string> Options { get; set; } = new();
}

/// <summary>
/// Represents form validation results.
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Stub implementation of form data service for the app template.
/// Replace with platform-specific implementation using your preferred form library.
/// </summary>
public class FormDataService : IFormDataService
{
    public async Task<FormDefinition> GetFormDefinitionAsync(string serviceId, int layerId)
    {
        // Return a sample form definition
        return await Task.FromResult(new FormDefinition
        {
            Id = $"{serviceId}_{layerId}",
            Name = "Field Data Collection",
            Description = "Collect field observations and measurements",
            Fields = new List<FormField>
            {
                new() { Name = "Name", Label = "Feature Name", Type = "text", Required = true },
                new() { Name = "Type", Label = "Feature Type", Type = "select", Required = true,
                       Options = ["Point of Interest", "Observation", "Measurement", "Sample"] },
                new() { Name = "Description", Label = "Description", Type = "textarea", Required = false },
                new() { Name = "Value", Label = "Measurement Value", Type = "number", Required = false },
                new() { Name = "Units", Label = "Units", Type = "text", Required = false },
                new() { Name = "Photo", Label = "Photo", Type = "file", Required = false }
            }
        });
    }

    public async Task<ValidationResult> ValidateFormDataAsync(FormDefinition form, Dictionary<string, object> data)
    {
        var result = new ValidationResult { IsValid = true };

        foreach (var field in form.Fields.Where(f => f.Required))
        {
            if (!data.ContainsKey(field.Name) || data[field.Name] == null || string.IsNullOrWhiteSpace(data[field.Name].ToString()))
            {
                result.IsValid = false;
                result.Errors.Add($"{field.Label} is required");
            }
        }

        return await Task.FromResult(result);
    }

    public async Task<Feature> CreateFeatureFromFormAsync(FormDefinition form, Dictionary<string, object> data, byte[]? geometry = null)
    {
        return await Task.FromResult(new Feature
        {
            ObjectId = Random.Shared.Next(10000, 99999), // Temporary ID
            Geometry = geometry,
            Attributes = data
        });
    }

    public async Task SaveDraftAsync(string draftId, Dictionary<string, object> data)
    {
        // TODO: Implement draft persistence to local storage
        await Task.CompletedTask;
    }

    public async Task<Dictionary<string, object>?> LoadDraftAsync(string draftId)
    {
        // TODO: Implement draft loading from local storage
        return await Task.FromResult<Dictionary<string, object>?>(null);
    }
}