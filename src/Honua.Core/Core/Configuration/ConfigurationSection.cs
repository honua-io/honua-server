// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Configuration;

public class ConfigurationSection
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<ConfigurationProperty> Properties { get; set; } = new();

    public List<ConfigurationSection> SubSections { get; set; } = new();

    public bool IsRequired { get; set; }

    public Dictionary<string, object?> Examples { get; set; } = new();
}

public class ConfigurationProperty
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public object? DefaultValue { get; set; }

    public object? CurrentValue { get; set; }

    public bool IsRequired { get; set; }

    public string? EnvironmentVariable { get; set; }

    public bool IsSensitive { get; set; }

    public List<string> ValidationRules { get; set; } = new();

    public string? Validation { get; set; }

    public string? Source { get; set; }
}

public class EnvironmentVariableInfo
{
    public string Name { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? Default { get; set; }

    public bool Required { get; set; }

    public string? Example { get; set; }

    public List<string> ExampleValues { get; set; } = new();
}

public class ConfigurationDocumentation
{
    public List<ConfigurationSection> Sections { get; set; } = new();

    public string Version { get; set; } = string.Empty;

    public List<EnvironmentVariableInfo> EnvironmentVariables { get; set; } = new();

    public string Environment { get; set; } = string.Empty;

    public Dictionary<string, string> Metadata { get; set; } = new();
}
