// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Stable, machine-readable codes used by the v1 extrusion metadata
/// contract to report invalid <see cref="LayerExtrusionInfo"/> input.
/// Codes are part of the public API contract and must not be renamed
/// across releases.
/// </summary>
public static class ExtrusionErrorCodes
{
    /// <summary>The required height field name was missing or empty.</summary>
    public const string HeightFieldMissing = "EXTRUSION_HEIGHT_FIELD_MISSING";

    /// <summary>The configured height field does not exist on the layer.</summary>
    public const string HeightFieldNotFound = "EXTRUSION_HEIGHT_FIELD_NOT_FOUND";

    /// <summary>The configured height field is not a numeric type supported for extrusion.</summary>
    public const string HeightFieldTypeInvalid = "EXTRUSION_HEIGHT_FIELD_TYPE_INVALID";

    /// <summary>The configured base-height field does not exist on the layer.</summary>
    public const string BaseFieldNotFound = "EXTRUSION_BASE_FIELD_NOT_FOUND";

    /// <summary>The configured base-height field is not a numeric type supported for extrusion.</summary>
    public const string BaseFieldTypeInvalid = "EXTRUSION_BASE_FIELD_TYPE_INVALID";

    /// <summary>The configured vertical unit is not a recognized value.</summary>
    public const string UnitUnrecognized = "EXTRUSION_UNIT_UNRECOGNIZED";

    /// <summary>The configured default height is negative.</summary>
    public const string NegativeDefaultHeight = "EXTRUSION_NEGATIVE_DEFAULT_HEIGHT";
}
