// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Represents a configuration section for documentation purposes.
/// </summary>
public class ConfigurationSection
{
    /// <summary>
    /// Gets or sets the name of the configuration section.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the configuration section.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configuration properties in this section.
    /// </summary>
    public List<ConfigurationProperty> Properties { get; set; } = new();

    /// <summary>
    /// Gets or sets child sections.
    /// </summary>
    public List<ConfigurationSection> SubSections { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this section is required.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Gets or sets example values for this section.
    /// </summary>
    public Dictionary<string, object?> Examples { get; set; } = new();
}

/// <summary>
/// Represents a configuration property for documentation purposes.
/// </summary>
public class ConfigurationProperty
{
    /// <summary>
    /// Gets or sets the name of the property.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the property.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type of the property.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default value of the property.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this property is required.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Gets or sets the environment variable information for this property.
    /// </summary>
    public EnvironmentVariableInfo? EnvironmentVariable { get; set; }

    /// <summary>
    /// Gets or sets validation information for this property.
    /// </summary>
    public List<string> ValidationRules { get; set; } = new();
}

/// <summary>
/// Represents environment variable information for configuration documentation.
/// </summary>
public class EnvironmentVariableInfo
{
    /// <summary>
    /// Gets or sets the environment variable name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the environment variable.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets example values for the environment variable.
    /// </summary>
    public List<string> ExampleValues { get; set; } = new();
}

/// <summary>
/// Represents configuration documentation for the entire application.
/// </summary>
public class ConfigurationDocumentation
{
    /// <summary>
    /// Gets or sets the configuration sections.
    /// </summary>
    public List<ConfigurationSection> Sections { get; set; } = new();

    /// <summary>
    /// Gets or sets the version of the configuration schema.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets general configuration information.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}