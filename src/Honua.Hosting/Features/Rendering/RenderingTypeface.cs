// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

// The Lambda image uses the Skia asset that has no fontconfig lookup, so it needs an explicit font file.
internal static class RenderingTypeface
{
    internal const string FontPathEnvironmentVariable = "HONUA_DEFAULT_FONT_PATH";

    private static readonly Lazy<SKTypeface?> DefaultTypeface = new(
        () => Load(Environment.GetEnvironmentVariable(FontPathEnvironmentVariable)),
        LazyThreadSafetyMode.ExecutionAndPublication);

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
