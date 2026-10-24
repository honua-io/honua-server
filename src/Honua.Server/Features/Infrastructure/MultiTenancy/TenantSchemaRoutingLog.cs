// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.MultiTenancy;

internal static partial class TenantSchemaRoutingLog
{
    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Debug,
        Message = "Routed tenant '{TenantId}' to schema '{Schema}' for path '{Path}'.")]
    public static partial void Routed(ILogger logger, string tenantId, string schema, string? path);
}
