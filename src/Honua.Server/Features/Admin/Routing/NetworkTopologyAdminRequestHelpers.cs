// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Routing;

internal readonly struct HeaderResolutionResult<T>
{
    public T? Value { get; init; }
    public string Error { get; init; }
    public bool IsValid => string.IsNullOrEmpty(Error);

    public static HeaderResolutionResult<T> Success(T value) => new() { Value = value, Error = string.Empty };

    public static HeaderResolutionResult<T> Failure(string error) => new() { Value = default, Error = error };
}

internal static class NetworkTopologyAdminRequestHelpers
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IfMatchHeader = "If-Match";
    private const int MaxIdempotencyKeyLength = 200;

    public static HeaderResolutionResult<string> ResolveIdempotencyKey(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var values) || values.Count == 0)
        {
            return HeaderResolutionResult<string>.Failure($"{IdempotencyKeyHeader} header is required.");
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HeaderResolutionResult<string>.Failure($"{IdempotencyKeyHeader} header must not be empty.");
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            return HeaderResolutionResult<string>.Failure(
                $"{IdempotencyKeyHeader} header must be at most {MaxIdempotencyKeyLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            return HeaderResolutionResult<string>.Failure(
                $"{IdempotencyKeyHeader} header must not contain control characters.");
        }

        return HeaderResolutionResult<string>.Success(trimmed);
    }

    public static HeaderResolutionResult<long> ResolveExpectedRowVersion(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(IfMatchHeader, out var values) || values.Count == 0)
        {
            return HeaderResolutionResult<long>.Failure(
                $"{IfMatchHeader} header is required and must carry the generation's current row version.");
        }

        var raw = values[0]?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw, out var rowVersion) || rowVersion <= 0)
        {
            return HeaderResolutionResult<long>.Failure(
                $"{IfMatchHeader} header must be a positive integer row version.");
        }

        return HeaderResolutionResult<long>.Success(rowVersion);
    }
}
