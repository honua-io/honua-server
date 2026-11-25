// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Admin.Scene;

internal readonly struct FormFieldParseResult<T>
{
    public T Value { get; init; }
    public string? Error { get; init; }
    public bool IsValid => Error is null;

    public static FormFieldParseResult<T> Success(T value) => new() { Value = value };

    public static FormFieldParseResult<T> Failure(string error) => new() { Value = default!, Error = error };
}

internal static class SceneIngestFormHelpers
{
    public static FormFieldParseResult<int?> ParseOptionalInt(IFormCollection form, string key)
    {
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return FormFieldParseResult<int?>.Success(null);
        }

        if (!int.TryParse(raw[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return FormFieldParseResult<int?>.Failure($"{key} must be an integer.");
        }

        return FormFieldParseResult<int?>.Success(parsed);
    }

    public static FormFieldParseResult<bool?> ParseOptionalBool(IFormCollection form, string key)
    {
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return FormFieldParseResult<bool?>.Success(null);
        }

        if (!bool.TryParse(raw[0], out var parsed))
        {
            return FormFieldParseResult<bool?>.Failure($"{key} must be 'true' or 'false'.");
        }

        return FormFieldParseResult<bool?>.Success(parsed);
    }
}
