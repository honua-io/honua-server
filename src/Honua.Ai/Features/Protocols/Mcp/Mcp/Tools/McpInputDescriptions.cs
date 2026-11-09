// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpInputDescriptions
{
    internal static string Describe(string operationId, string parameter, string description) =>
        (operationId, parameter) switch
        {
            (McpConnectionDiscoveryProjection.LayersOperationId, "id") =>
                "Connection id from honua_admin_connections_list.",
            ("admin.layer.set-enabled", "layerId") =>
                "Storage layerId from honua_admin_connection_layers_list; not a publication index from honua_list_layers.",
            _ => description
        };
}
