// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Models;

internal sealed record McpSkillResource(string Uri, string Digest, long Size);

internal sealed record McpSkill(string Uri, JsonElement Frontmatter, IReadOnlyList<McpSkillResource> Resources);

internal sealed class McpSkillsListResult
{
    public required IReadOnlyList<McpSkill> Skills { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }
}

internal sealed record McpSkillsGetResult(McpSkill Skill);
