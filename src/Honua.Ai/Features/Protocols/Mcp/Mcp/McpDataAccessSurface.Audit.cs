// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

internal sealed partial class McpDataAccessSurface
{
    private Task AuditAsync(HttpContext httpContext, McpAccess access) =>
        McpAccessAudit.RecordAsync(httpContext, access, _logger);
}
