// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Information about editor tracking fields on a layer
/// </summary>
public sealed class EditFieldsInfo
{
    /// <summary>
    /// Field name that tracks the creator
    /// </summary>
    public string? CreatorField { get; init; }

    /// <summary>
    /// Field name that tracks the creation date
    /// </summary>
    public string? CreationDateField { get; init; }

    /// <summary>
    /// Field name that tracks the last editor
    /// </summary>
    public string? EditorField { get; init; }

    /// <summary>
    /// Field name that tracks the last edit date
    /// </summary>
    public string? EditDateField { get; init; }
}

/// <summary>
/// Information about the last edit on a layer
/// </summary>
public sealed class EditingInfo
{
    /// <summary>
    /// Unix timestamp (milliseconds) of the last edit, or null if unknown
    /// </summary>
    public long? LastEditDate { get; init; }
}

/// <summary>
/// Unique identifier field metadata per the GeoServices REST specification
/// </summary>
public sealed class UniqueIdFieldInfo
{
    /// <summary>
    /// Name of the unique identifier field
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the field value is system-maintained (auto-generated)
    /// </summary>
    public bool IsSystemMaintained { get; init; }
}

/// <summary>
/// Feature template for creating new features
/// </summary>
public sealed class FeatureTemplate
{
    /// <summary>
    /// Template name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Template description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Default drawing tool for the template
    /// </summary>
    public string? DrawingTool { get; init; }

    /// <summary>
    /// Prototype attributes for new features created with this template
    /// </summary>
    public object? Prototype { get; init; }
}
