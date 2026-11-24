// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Routing;

internal static class NetworkTopologyAdminRequestHelpers
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IfMatchHeader = "If-Match";
    private const int MaxIdempotencyKeyLength = 200;

    public static bool TryResolveIdempotencyKey(HttpContext context, out string key, out string error)
    {
        key = string.Empty;
        error = string.Empty;

        if (!context.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var values) || values.Count == 0)
        {
            error = $"{IdempotencyKeyHeader} header is required.";
            return false;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{IdempotencyKeyHeader} header must not be empty.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            error = $"{IdempotencyKeyHeader} header must be at most {MaxIdempotencyKeyLength} characters.";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = $"{IdempotencyKeyHeader} header must not contain control characters.";
            return false;
        }

        key = trimmed;
        return true;
    }

    public static bool TryResolveExpectedRowVersion(HttpContext context, out long rowVersion, out string error)
    {
        rowVersion = 0;
        error = string.Empty;

        if (!context.Request.Headers.TryGetValue(IfMatchHeader, out var values) || values.Count == 0)
        {
            error = $"{IfMatchHeader} header is required and must carry the generation's current row version.";
            return false;
        }

        var raw = values[0]?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw, out rowVersion) || rowVersion <= 0)
        {
            error = $"{IfMatchHeader} header must be a positive integer row version.";
            return false;
        }

        return true;
    }
}
