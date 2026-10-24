// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.Cli.Release;

/// <summary>Validates candidate-bound test results before asserting a passing receipt in a lock.</summary>
internal static class GpValidationReceipt
{
    public static string? Validate(string path, string name, string sourceSha, string runtimeDigest, List<string> errors)
    {
        var label = $"validation.receipts[{name}]";
        if (new FileInfo(path).Length > 65536)
        {
            errors.Add($"{label} exceeds the 64 KiB receipt limit");
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var bytes = new byte[65537];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > 65536)
            {
                errors.Add($"{label} exceeds the 64 KiB receipt limit");
                return null;
            }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasUniqueFields(root) ||
                Text(root, "schemaVersion") != "honua.gp-validation/v1" ||
                Text(root, "status") != "pass" ||
                Text(root, "sourceSha") != sourceSha ||
                Text(root, "runtimeImageDigest") != runtimeDigest ||
                !root.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Object ||
                !HasUniqueFields(tests) || !Count(tests, "passed", out var passed) || passed <= 0 ||
                !Count(tests, "failed", out var failed) || failed != 0 ||
                !Count(tests, "skipped", out var skipped) || skipped != 0)
            {
                errors.Add($"{label} must be honua.gp-validation/v1 with status pass, exact sourceSha/runtimeImageDigest, passed > 0, failed = 0 and skipped = 0");
                return null;
            }
            return GpProcessReleaseLock.Sha256Of(bytes.AsSpan(0, length));
        }
        catch (JsonException)
        {
            errors.Add($"{label} is not a valid honua.gp-validation/v1 JSON receipt");
            return null;
        }
    }

    private static bool HasUniqueFields(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static string? Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static bool Count(JsonElement value, string name, out int count)
    {
        count = 0;
        return value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out count);
    }
}
