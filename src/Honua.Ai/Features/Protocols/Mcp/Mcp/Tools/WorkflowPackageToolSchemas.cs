// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class WorkflowPackageToolSchemas
{
    public static readonly JsonElement AuthoringOutput = JsonDocument.Parse("""
        {"type":"object","required":["qualification"],"properties":{
          "qualification":{"type":"string"},"packages":{"type":["array","null"],"items":{"type":"object"}},
          "version":{"type":["object","null"],"properties":{
            "packageId":{"type":"string"},"version":{"type":"integer","minimum":1},"packageHash":{"type":"string"},
            "graph":{"type":"object"},"validation":{"type":"object"},"ownership":{"type":["object","null"]},
            "createdBy":{"type":["string","null"]},"createdAt":{"type":"string","format":"date-time"}}}}}
        """).RootElement.Clone();
    public static readonly JsonElement GovernanceOutput = JsonDocument.Parse("""
        {"type":"object","required":["operationId","operationInstanceId","status"],"properties":{
          "operationId":{"type":"string"},"operationInstanceId":{"type":"string"},"status":{"type":"string"},
          "proposalId":{"type":["string","null"]},"correlationId":{"type":["string","null"]},
          "auditId":{"type":["string","null"]},"reason":{"type":["string","null"]},
          "result":{"type":["object","null"]},"createdAt":{"type":"string","format":"date-time"},
          "updatedAt":{"type":"string","format":"date-time"}}}
        """).RootElement.Clone();
}
