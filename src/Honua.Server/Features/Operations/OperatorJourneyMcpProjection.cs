// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;

namespace Honua.Server.Features.Operations;

internal sealed class OperatorJourneyMcpProjection : IAuditedAdminMcpProjection
{
    public IReadOnlyCollection<string> OperationIds { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "admin.server.status",
            "admin.connections.list",
            "admin.connections.tables.discover",
            "admin.connections.create",
            "admin.connections.test",
            "admin.import.upload-url",
            "admin.import.upload-object",
        };
}
