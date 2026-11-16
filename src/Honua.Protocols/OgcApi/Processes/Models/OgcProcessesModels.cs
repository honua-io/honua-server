// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.Ogc.Api.Processes.Models;

public sealed record OgcProcessSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("jobControlOptions")]
    public ImmutableArray<string>? JobControlOptions { get; init; }

    [JsonPropertyName("outputTransmission")]
    public ImmutableArray<string>? OutputTransmission { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

public sealed record OgcProcessList
{
    [JsonPropertyName("processes")]
    public required ImmutableArray<OgcProcessSummary> Processes { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

public sealed record OgcProcessIoDescription
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("minOccurs")]
    public int? MinOccurs { get; init; }

    [JsonPropertyName("schema")]
    public required OgcProcessIoSchema Schema { get; init; }
}

public sealed record OgcProcessIoSchema
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

    [JsonPropertyName("contentMediaType")]
    public string? ContentMediaType { get; init; }

    [JsonPropertyName("contentEncoding")]
    public string? ContentEncoding { get; init; }

    [JsonPropertyName("minimum")]
    public double? Minimum { get; init; }

    [JsonPropertyName("maximum")]
    public double? Maximum { get; init; }

    [JsonPropertyName("enum")]
    public ImmutableArray<string>? Enum { get; init; }

    [JsonPropertyName("items")]
    public OgcProcessIoSchema? Items { get; init; }

    [JsonPropertyName("minItems")]
    public int? MinItems { get; init; }

    [JsonPropertyName("maxItems")]
    public int? MaxItems { get; init; }

    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, OgcProcessIoSchema>? Properties { get; init; }

    [JsonPropertyName("required")]
    public ImmutableArray<string>? Required { get; init; }

    [JsonPropertyName("oneOf")]
    public ImmutableArray<OgcProcessIoSchema>? OneOf { get; init; }

    [JsonPropertyName("allOf")]
    public ImmutableArray<OgcProcessIoSchema>? AllOf { get; init; }

    [JsonPropertyName("not")]
    public OgcProcessIoSchema? Not { get; init; }
}

public sealed record OgcProcessDescription
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("jobControlOptions")]
    public ImmutableArray<string>? JobControlOptions { get; init; }

    [JsonPropertyName("outputTransmission")]
    public ImmutableArray<string>? OutputTransmission { get; init; }

    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, OgcProcessIoDescription>? Inputs { get; init; }

    [JsonPropertyName("outputs")]
    public IReadOnlyDictionary<string, OgcProcessIoDescription>? Outputs { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

public sealed record OgcExecuteRequest
{
    [JsonPropertyName("inputs")]
    public ImmutableDictionary<string, JsonElement>? Inputs { get; init; }

    [JsonPropertyName("outputs")]
    public ImmutableDictionary<string, JsonElement>? Outputs { get; init; }

    [JsonPropertyName("response")]
    public string? Response { get; init; }
}

public sealed record OgcStatusInfo
{
    [JsonPropertyName("processID")]
    public string? ProcessId { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "process";

    [JsonPropertyName("jobID")]
    public required string JobId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("created")]
    public string? Created { get; init; }

    [JsonPropertyName("updated")]
    public string? Updated { get; init; }

    [JsonPropertyName("progress")]
    public int? Progress { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

public sealed record OgcProcessError
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("capability")]
    public string? Capability { get; init; }

    [JsonPropertyName("missingDependency")]
    public string? MissingDependency { get; init; }

    [JsonPropertyName("missingEntitlement")]
    public string? MissingEntitlement { get; init; }

    [JsonPropertyName("remediation")]
    public string? Remediation { get; init; }

    [JsonPropertyName("remediationRef")]
    public string? RemediationRef { get; init; }
}

public sealed record OgcJobList
{
    [JsonPropertyName("jobs")]
    public required ImmutableArray<OgcStatusInfo> Jobs { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

public sealed record OgcResultsDocument
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Outputs { get; init; }
}
