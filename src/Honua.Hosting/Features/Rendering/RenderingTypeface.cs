// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Resolves the process-wide typeface used by headless rendering surfaces.
/// </summary>
/// <remarks>
/// Linux Lambda uses SkiaSharp's no-dependencies native asset, which cannot rely on
/// Fontconfig-backed family lookup. The image therefore configures an explicit font file.
/// Other hosts retain SkiaSharp's platform-default behavior.
/// </remarks>
internal static class RenderingTypeface
{
    internal const string FontPathEnvironmentVariable = "HONUA_DEFAULT_FONT_PATH";

    private static readonly Lazy<SKTypeface?> DefaultTypeface = new(
        () => Load(Environment.GetEnvironmentVariable(FontPathEnvironmentVariable)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the configured rendering typeface, or the platform default when no path is configured.
    /// </summary>
    internal static SKTypeface? Default => DefaultTypeface.Value;

    internal static SKTypeface? Load(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return SKTypeface.Default;
        }

        if (!File.Exists(configuredPath))
        {
            throw new InvalidOperationException(
                $"Configured rendering font does not exist: {configuredPath}");
        }

        return SKTypeface.FromFile(configuredPath)
            ?? throw new InvalidOperationException(
                $"SkiaSharp could not load the configured rendering font: {configuredPath}");
    }
}
