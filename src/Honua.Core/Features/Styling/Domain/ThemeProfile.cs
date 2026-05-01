// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Theme transforms applied by the style engine after a canonical MapLibre
/// style document has been generated.  Selection is deterministic: the same
/// style + theme always produces the same output.
/// </summary>
public enum ThemeProfile
{
    /// <summary>No theme transform is applied; the canonical style is returned as-is.</summary>
    Default = 0,

    /// <summary>Inverts lightness on color paint properties for dark-mode display.</summary>
    Dark = 1,

    /// <summary>Remaps fill/line/circle colors onto a colorblind-safe palette.</summary>
    ColorblindSafe = 2,

    /// <summary>Forces high-contrast solid output suitable for print rendering.</summary>
    Print = 3
}
