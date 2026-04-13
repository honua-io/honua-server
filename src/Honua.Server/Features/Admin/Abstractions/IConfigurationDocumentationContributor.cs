// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.Admin.Abstractions;

/// <summary>
/// Interface for contributing to configuration documentation.
/// </summary>
public interface IConfigurationDocumentationContributor
{
    /// <summary>
    /// Gets the configuration sections this contributor documents.
    /// </summary>
    IEnumerable<string> GetConfigurationSections();

    /// <summary>
    /// Generates documentation for the specified configuration section.
    /// </summary>
    /// <param name="sectionName">The configuration section name</param>
    /// <param name="configuration">The current configuration</param>
    /// <returns>Documentation for the section</returns>
    Task<ConfigurationSectionDocumentation> GenerateDocumentationAsync(string sectionName, IConfiguration configuration);
}

/// <summary>
/// Documentation for a configuration section.
/// </summary>
public sealed record ConfigurationSectionDocumentation(
    string SectionName,
    string Description,
    IReadOnlyDictionary<string, ConfigurationPropertyDocumentation> Properties,
    IReadOnlyList<string> Examples);

/// <summary>
/// Documentation for a configuration property.
/// </summary>
public sealed record ConfigurationPropertyDocumentation(
    string PropertyName,
    string Description,
    Type PropertyType,
    object? DefaultValue,
    bool IsRequired,
    string? ValidationRules);
