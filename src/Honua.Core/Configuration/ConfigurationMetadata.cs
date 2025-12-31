// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Represents the complete configuration documentation response.
/// </summary>
public sealed class ConfigurationDocumentation
{
    /// <summary>
    /// Configuration sections grouped by category.
    /// </summary>
    public required IReadOnlyList<ConfigurationSection> Sections { get; init; }

    /// <summary>
    /// Quick reference of all environment variables.
    /// </summary>
    public required IReadOnlyList<EnvironmentVariableInfo> EnvironmentVariables { get; init; }

    /// <summary>
    /// Server version information.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Current environment name.
    /// </summary>
    public required string Environment { get; init; }
}

/// <summary>
/// Represents a configuration section with its properties.
/// </summary>
public sealed class ConfigurationSection
{
    /// <summary>
    /// Section name (e.g., "Cache", "Limits", "Security").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable description of this section.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Configuration properties in this section.
    /// </summary>
    public required IReadOnlyList<ConfigurationProperty> Properties { get; init; }
}

/// <summary>
/// Represents a single configuration property.
/// </summary>
public sealed class ConfigurationProperty
{
    /// <summary>
    /// Property name (e.g., "Enabled", "DefaultTtlSeconds").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Full configuration path (e.g., "Cache:Enabled").
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Environment variable name (e.g., "Cache__Enabled").
    /// </summary>
    public required string EnvironmentVariable { get; init; }

    /// <summary>
    /// Property type (e.g., "boolean", "integer", "string").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Default value if not configured.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Current value (sensitive values are masked).
    /// </summary>
    public object? CurrentValue { get; init; }

    /// <summary>
    /// Whether this property is required.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// Whether this property contains sensitive data (value will be masked).
    /// </summary>
    public bool IsSensitive { get; init; }

    /// <summary>
    /// Validation constraints (e.g., "Range: 1-86400").
    /// </summary>
    public string? Validation { get; init; }

    /// <summary>
    /// Source of the current value (e.g., "Environment", "appsettings.json", "Default").
    /// </summary>
    public required string Source { get; init; }
}

/// <summary>
/// Quick reference information for an environment variable.
/// </summary>
public sealed class EnvironmentVariableInfo
{
    /// <summary>
    /// Environment variable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Configuration path this maps to.
    /// </summary>
    public required string ConfigPath { get; init; }

    /// <summary>
    /// Brief description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Default value.
    /// </summary>
    public string? Default { get; init; }

    /// <summary>
    /// Whether this is required.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Example value.
    /// </summary>
    public string? Example { get; init; }
}
