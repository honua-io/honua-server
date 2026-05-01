// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Describes a single symbolizer input that the style engine could not
/// translate to the canonical MapLibre Style Spec v8 form.  Surfaces the
/// stable code, the originating symbolizer/renderer type, and operator-facing
/// guidance so the caller can respond without parsing free-form messages.
/// </summary>
public sealed record UnsupportedSymbolizerInfo
{
    /// <summary>Stable machine-readable code from <see cref="StyleErrorCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>Renderer or symbol type as supplied by the caller; empty when the type was missing.</summary>
    public required string SymbolizerType { get; init; }

    /// <summary>Operator-facing guidance on what to do next.</summary>
    public required string Guidance { get; init; }
}
