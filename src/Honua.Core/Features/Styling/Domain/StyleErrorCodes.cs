// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Stable, machine-readable codes used by the style engine to report
/// symbolizer inputs that cannot be losslessly converted to the canonical
/// MapLibre Style Spec v8 form.  Codes are part of the public API contract
/// and must not be renamed across releases.
/// </summary>
public static class StyleErrorCodes
{
    /// <summary>The submitted GeoServices renderer type is not supported.</summary>
    public const string RendererTypeUnsupported = "RENDERER_TYPE_UNSUPPORTED";

    /// <summary>The submitted GeoServices symbol type is not supported.</summary>
    public const string SymbolTypeUnsupported = "SYMBOL_TYPE_UNSUPPORTED";

    /// <summary>A renderer carried picture marker symbols whose payload was only partially preserved.</summary>
    public const string PictureMarkerPartial = "PICTURE_MARKER_PARTIAL";

    /// <summary>The submitted renderer is structurally valid but missing required fields and was treated as the default style.</summary>
    public const string RendererPayloadIncomplete = "RENDERER_PAYLOAD_INCOMPLETE";
}
