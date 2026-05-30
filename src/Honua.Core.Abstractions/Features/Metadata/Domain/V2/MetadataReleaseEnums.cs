// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Semantic artifact groups exposed by metadata inventory and release packages.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataSemanticArtifactKind>))]
public enum MetadataSemanticArtifactKind
{
    /// <summary>A canonical Metadata v2 resource.</summary>
    [JsonStringEnumMemberName("resource")]
    Resource,

    /// <summary>A service that publishes resources.</summary>
    [JsonStringEnumMemberName("service")]
    Service,

    /// <summary>A publication that binds a resource to a service.</summary>
    [JsonStringEnumMemberName("publication")]
    Publication,

    /// <summary>A field owned by a resource schema.</summary>
    [JsonStringEnumMemberName("field")]
    Field,

    /// <summary>A catalog target.</summary>
    [JsonStringEnumMemberName("catalog")]
    Catalog,

    /// <summary>A Metadata v2 policy.</summary>
    [JsonStringEnumMemberName("policy")]
    Policy,

    /// <summary>A Metadata v2 role.</summary>
    [JsonStringEnumMemberName("role")]
    Role,
}

/// <summary>
/// Binding state for a semantic artifact in a requested environment.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataEnvironmentBindingState>))]
public enum MetadataEnvironmentBindingState
{
    /// <summary>The environment contains a binding for the semantic artifact.</summary>
    [JsonStringEnumMemberName("bound")]
    Bound,

    /// <summary>The environment is available but the semantic artifact is absent.</summary>
    [JsonStringEnumMemberName("missing")]
    Missing,

    /// <summary>The requested environment has no active Metadata v2 snapshot.</summary>
    [JsonStringEnumMemberName("environment-unavailable")]
    EnvironmentUnavailable,
}

/// <summary>
/// Coarse class of change represented by a release entry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataReleaseChangeClass>))]
public enum MetadataReleaseChangeClass
{
    /// <summary>Metadata-only change.</summary>
    [JsonStringEnumMemberName("metadata")]
    Metadata,

    /// <summary>Content-version change.</summary>
    [JsonStringEnumMemberName("content")]
    Content,

    /// <summary>Environment binding or physical target change.</summary>
    [JsonStringEnumMemberName("binding")]
    Binding,

    /// <summary>RBAC policy or role change.</summary>
    [JsonStringEnumMemberName("policy")]
    Policy,

    /// <summary>Deletion or retirement of the artifact.</summary>
    [JsonStringEnumMemberName("delete")]
    Delete,
}

/// <summary>
/// Lifecycle state for a metadata release package.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataReleasePackageStatus>))]
public enum MetadataReleasePackageStatus
{
    /// <summary>Draft package that has not been staged.</summary>
    [JsonStringEnumMemberName("draft")]
    Draft,

    /// <summary>Package is ready for downstream GitOps processing.</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>Package has been staged by downstream automation.</summary>
    [JsonStringEnumMemberName("staged")]
    Staged,

    /// <summary>Package was superseded by a newer package.</summary>
    [JsonStringEnumMemberName("superseded")]
    Superseded,

    /// <summary>Package was cancelled before promotion.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

/// <summary>
/// Per-entry release state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataReleaseEntryStatus>))]
public enum MetadataReleaseEntryStatus
{
    /// <summary>Entry is included in a draft release package.</summary>
    [JsonStringEnumMemberName("draft")]
    Draft,

    /// <summary>Entry is ready for downstream processing.</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,
}
