// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Admin.Scene;

internal static class SceneIngestFormHelpers
{
    public static bool TryParseOptionalInt(
        IFormCollection form, string key, out int? value, out string? error)
    {
        value = null;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }

        if (!int.TryParse(raw[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseOptionalBool(
        IFormCollection form, string key, out bool? value, out string? error)
    {
        value = null;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }

        if (!bool.TryParse(raw[0], out var parsed))
        {
            error = $"{key} must be 'true' or 'false'.";
            return false;
        }

        value = parsed;
        return true;
    }
}
