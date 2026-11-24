// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Server.Features.Forms;

// The idempotency key is part of a unique btree index and the client id is kept in the JSONB
// submission summary. Postgres rejects over-long index rows, NUL characters and unpaired
// surrogates, so these are refused with 400 before the first write. The limits match the
// field-collection sync API.
internal static class FormSubmissionInputLimits
{
    internal const int MaxIdempotencyKeyLength = 256;
    internal const int MaxClientIdLength = 128;

    internal static string? FindIdentifierError(FormSubmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FindError("idempotencyKey", request.IdempotencyKey, MaxIdempotencyKeyLength)
            ?? FindError("clientId", request.ClientId, MaxClientIdLength);
    }

    private static string? FindError(string field, string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            return $"Field '{field}' must be at most {maxLength} characters.";
        }

        return HasStorableText(value)
            ? null
            : $"Field '{field}' must not contain control characters or unpaired surrogates.";
    }

    private static bool HasStorableText(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done
                || Rune.IsControl(rune))
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
