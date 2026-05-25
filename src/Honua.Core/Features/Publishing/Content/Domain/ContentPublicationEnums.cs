// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Content.Domain;

/// <summary>
/// Category of Studio-generated artifact owned by the content publication registry.
/// Closed enum so the contract is AOT- and trimming-safe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationKind>))]
public enum ContentPublicationKind
{
    /// <summary>A published interactive map.</summary>
    [JsonStringEnumMemberName("map")]
    Map,

    /// <summary>A published dashboard.</summary>
    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    /// <summary>A published report.</summary>
    [JsonStringEnumMemberName("report")]
    Report,

    /// <summary>A published generated application.</summary>
    [JsonStringEnumMemberName("generated-app")]
    GeneratedApp
}

/// <summary>
/// Server-owned visibility scope of a published route.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationVisibility>))]
public enum ContentPublicationVisibility
{
    /// <summary>Only the owner / explicit grants can read.</summary>
    [JsonStringEnumMemberName("private")]
    Private,

    /// <summary>Any authenticated member of the organization can read.</summary>
    [JsonStringEnumMemberName("organization")]
    Organization,

    /// <summary>Members of the associated team can read.</summary>
    [JsonStringEnumMemberName("team")]
    Team,

    /// <summary>Anyone, including anonymous callers, can read.</summary>
    [JsonStringEnumMemberName("public")]
    Public
}

/// <summary>
/// Lifecycle state of a published route pointer.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationLifecycle>))]
public enum ContentPublicationLifecycle
{
    /// <summary>Route resolves to its active version and serves reads.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Route is temporarily suspended; reads are denied with 404/403.</summary>
    [JsonStringEnumMemberName("suspended")]
    Suspended,

    /// <summary>Route is archived; the pointer is retained for history only.</summary>
    [JsonStringEnumMemberName("archived")]
    Archived
}

/// <summary>
/// Operation recorded against a publication in the append-only event log.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationOperation>))]
public enum ContentPublicationOperation
{
    /// <summary>Initial publish that claimed the route.</summary>
    [JsonStringEnumMemberName("publish")]
    Publish,

    /// <summary>A new immutable version was created and the route pointer moved.</summary>
    [JsonStringEnumMemberName("republish")]
    Republish,

    /// <summary>The route pointer moved back to an earlier immutable version.</summary>
    [JsonStringEnumMemberName("rollback")]
    Rollback,

    /// <summary>Server-owned share/embed/public-link policy changed.</summary>
    [JsonStringEnumMemberName("policy-update")]
    PolicyUpdate,

    /// <summary>A public link id/hash was created.</summary>
    [JsonStringEnumMemberName("public-link-create")]
    PublicLinkCreate,

    /// <summary>A public link was revoked.</summary>
    [JsonStringEnumMemberName("public-link-revoke")]
    PublicLinkRevoke,

    /// <summary>A public-link read attempt was denied.</summary>
    [JsonStringEnumMemberName("public-link-denied")]
    PublicLinkDenied,

    /// <summary>An embed read attempt was denied by embed policy.</summary>
    [JsonStringEnumMemberName("embed-denied")]
    EmbedDenied
}

/// <summary>
/// Category of a publication dependency reference. References are stored by id only;
/// the dependency payload is never copied into the registry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationDependencyKind>))]
public enum ContentPublicationDependencyKind
{
    /// <summary>Metadata v2 service id.</summary>
    [JsonStringEnumMemberName("service")]
    Service,

    /// <summary>Metadata v2 resource id (dataset/layer source).</summary>
    [JsonStringEnumMemberName("resource")]
    Resource,

    /// <summary>Metadata v2 publication id.</summary>
    [JsonStringEnumMemberName("publication")]
    Publication,

    /// <summary>A published service record id.</summary>
    [JsonStringEnumMemberName("published-service")]
    PublishedService,

    /// <summary>A deployment id.</summary>
    [JsonStringEnumMemberName("deployment")]
    Deployment,

    /// <summary>A map package id.</summary>
    [JsonStringEnumMemberName("map-package")]
    MapPackage,

    /// <summary>An application package id.</summary>
    [JsonStringEnumMemberName("app-package")]
    AppPackage,

    /// <summary>A geoprocessing result package id.</summary>
    [JsonStringEnumMemberName("result-package")]
    ResultPackage,

    /// <summary>A report artifact id.</summary>
    [JsonStringEnumMemberName("report")]
    Report,

    /// <summary>A Console provenance item id.</summary>
    [JsonStringEnumMemberName("provenance-item")]
    ProvenanceItem
}
