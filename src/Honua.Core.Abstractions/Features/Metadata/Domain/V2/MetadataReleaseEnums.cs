// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

[JsonConverter(typeof(JsonStringEnumConverter<MetadataSemanticArtifactKind>))]
public enum MetadataSemanticArtifactKind
{
    [JsonStringEnumMemberName("resource")]
    Resource,

    [JsonStringEnumMemberName("service")]
    Service,

    [JsonStringEnumMemberName("publication")]
    Publication,

    [JsonStringEnumMemberName("field")]
    Field,

    [JsonStringEnumMemberName("catalog")]
    Catalog,

    [JsonStringEnumMemberName("policy")]
    Policy,

    [JsonStringEnumMemberName("role")]
    Role,

    [JsonStringEnumMemberName("workflow")]
    Workflow,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataEnvironmentBindingState>))]
public enum MetadataEnvironmentBindingState
{
    [JsonStringEnumMemberName("bound")]
    Bound,

    [JsonStringEnumMemberName("missing")]
    Missing,

    [JsonStringEnumMemberName("environment-unavailable")]
    EnvironmentUnavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataReleaseChangeClass>))]
public enum MetadataReleaseChangeClass
{
    [JsonStringEnumMemberName("metadata")]
    Metadata,

    [JsonStringEnumMemberName("content")]
    Content,

    [JsonStringEnumMemberName("binding")]
    Binding,

    [JsonStringEnumMemberName("policy")]
    Policy,

    [JsonStringEnumMemberName("delete")]
    Delete,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataReleasePackageStatus>))]
public enum MetadataReleasePackageStatus
{
    [JsonStringEnumMemberName("draft")]
    Draft,

    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("staged")]
    Staged,

    [JsonStringEnumMemberName("superseded")]
    Superseded,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataReleaseEntryStatus>))]
public enum MetadataReleaseEntryStatus
{
    [JsonStringEnumMemberName("draft")]
    Draft,

    [JsonStringEnumMemberName("ready")]
    Ready,
}
