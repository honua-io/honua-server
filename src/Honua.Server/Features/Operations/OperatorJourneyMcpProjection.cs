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
            "admin.connections.layers.list",
            "admin.connections.create",
            "admin.connections.test",
            "admin.import.upload-url",
            "admin.import.upload-object",
            "dataset.stage.preview",
            "dataset.stage.import",
            "dataset.stage.discard",
            "admin.import.formats",
            "admin.import.limits",
            "admin.import.preview",
            "admin.import.preview-url",
            "admin.connections.tables.validate",

            // Data handoff: an agent exports a Layer as files and reads the Export without leaving MCP.
            // create and delete always file a proposal for an agent (AgentApprovalRule).
            "admin.exports.list",
            "admin.exports.get",
            "admin.exports.create",
            "admin.exports.delete",
        };
}
