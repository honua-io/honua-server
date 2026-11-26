// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Infrastructure.Validation;

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
        return PostgresTextValidation.FindSingleLineError("idempotencyKey", request.IdempotencyKey, MaxIdempotencyKeyLength)
            ?? PostgresTextValidation.FindSingleLineError("clientId", request.ClientId, MaxClientIdLength);
    }
}
