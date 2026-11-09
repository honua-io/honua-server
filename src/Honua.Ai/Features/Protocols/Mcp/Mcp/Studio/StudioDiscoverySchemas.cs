// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Studio;

internal static class StudioDiscoverySchemas
{
    public static readonly JsonElement ListItems = Parse("""
        {"type":"object","additionalProperties":false,"properties":{
        "q":{"type":"string","maxLength":200},"cursor":{"type":"string","maxLength":2000},
        "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}}}
        """);
    public static readonly JsonElement ListVersions = Parse("""
        {"type":"object","required":["itemId"],"additionalProperties":false,"properties":{
        "itemId":{"type":"string","format":"uuid"},"beforeVersionNumber":{"type":"integer","minimum":1},
        "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}}}
        """);
    public static readonly JsonElement ItemsOutput = Parse("""
        {"type":"object","required":["items","total"],"properties":{
        "items":{"type":"array","items":{"type":"object","required":["itemId","packageKey","family","state"],"properties":{
        "itemId":{"type":"string","format":"uuid"},"packageKey":{"type":"string"},"family":{"type":"string"},
        "state":{"type":"string"},"currentVersionId":{"type":["string","null"]},"publishedVersionId":{"type":["string","null"]}}}},
        "total":{"type":"integer","minimum":0},"nextCursor":{"type":["string","null"]}}}
        """);
    public static readonly JsonElement VersionsOutput = Parse("""
        {"type":"object","required":["itemId","versions"],"properties":{
        "itemId":{"type":"string","format":"uuid"},"versions":{"type":"array","items":{"type":"object",
        "required":["versionId","versionNumber","contentHash","createdAt"],"properties":{
        "versionId":{"type":"string","format":"uuid"},"versionNumber":{"type":"integer","minimum":1},
        "contentHash":{"type":"string","minLength":1},"createdAt":{"type":"string","format":"date-time"},
        "changeNote":{"type":["string","null"]}}}},"nextBeforeVersionNumber":{"type":["integer","null"],"minimum":1}}}
        """);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
