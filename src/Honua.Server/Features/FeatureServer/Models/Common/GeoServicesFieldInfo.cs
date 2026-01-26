// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Field definition in GeoServices format, extending shared field metadata
/// </summary>
public sealed record GeoServicesFieldInfo : FieldDefinitionBase
{
    /// <summary>
    /// SQL type name
    /// </summary>
    public string? SqlType { get; init; }

    /// <summary>
    /// Field domain (for coded values)
    /// </summary>
    public object? Domain { get; init; }

    /// <summary>
    /// Whether the field is editable
    /// </summary>
    public bool Editable { get; init; } = true;

    /// <summary>
    /// Whether the field is visible
    /// </summary>
    public bool Visible { get; init; } = true;

    /// <summary>
    /// Whether the field can be used for display
    /// </summary>
    public bool CanSort { get; init; } = true;

    /// <summary>
    /// Determines if this field represents a string type based on GeoServices type
    /// </summary>
    protected override bool IsStringType()
    {
        return Type.Equals("esriFieldTypeString", StringComparison.OrdinalIgnoreCase);
    }
}
