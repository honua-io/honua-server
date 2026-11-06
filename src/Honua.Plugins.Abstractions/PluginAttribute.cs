// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public PluginAttribute(string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Id = id;
        Version = version;
    }

    public string Id { get; }

    public string Version { get; }

    public string? Description { get; set; }

    public PluginCapability Capabilities { get; set; } = PluginCapability.None;

    public string? DependsOnCsv { get; set; }

    public string? MinimumServerVersion { get; set; }
}
