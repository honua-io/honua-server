// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal static class OperationHandleResponseHeaders
{
    public static void SetOperationHeaders(HttpContext context, OperationHandle operation)
    {
        context.Response.Headers["X-Honua-Operation-Instance-Id"] = operation.OperationInstanceId;
        context.Response.Headers["X-Honua-Operation-Correlation-Id"] = operation.CorrelationId;
        if (!string.IsNullOrWhiteSpace(operation.AuditId))
        {
            context.Response.Headers["X-Honua-Operation-Audit-Id"] = operation.AuditId;
        }
    }
}
