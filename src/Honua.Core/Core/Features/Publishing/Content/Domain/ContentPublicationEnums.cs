// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Content.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationKind>))]
public enum ContentPublicationKind
{
    [JsonStringEnumMemberName("map")]
    Map,

    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    [JsonStringEnumMemberName("report")]
    Report,

    [JsonStringEnumMemberName("app")]
    App
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationVisibility>))]
public enum ContentPublicationVisibility
{
    [JsonStringEnumMemberName("private")]
    Private,

    [JsonStringEnumMemberName("organization")]
    Organization,

    [JsonStringEnumMemberName("team")]
    Team,

    [JsonStringEnumMemberName("public")]
    Public
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationLifecycle>))]
public enum ContentPublicationLifecycle
{
    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("suspended")]
    Suspended,

    [JsonStringEnumMemberName("archived")]
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationOperation>))]
public enum ContentPublicationOperation
{
    [JsonStringEnumMemberName("publish")]
    Publish,

    [JsonStringEnumMemberName("republish")]
    Republish,

    [JsonStringEnumMemberName("rollback")]
    Rollback,

    [JsonStringEnumMemberName("policy-update")]
    PolicyUpdate,

    [JsonStringEnumMemberName("public-link-create")]
    PublicLinkCreate,

    [JsonStringEnumMemberName("public-link-revoke")]
    PublicLinkRevoke,

    [JsonStringEnumMemberName("public-link-denied")]
    PublicLinkDenied,

    [JsonStringEnumMemberName("embed-denied")]
    EmbedDenied
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentPublicationDependencyKind>))]
public enum ContentPublicationDependencyKind
{
    [JsonStringEnumMemberName("service")]
    Service,

    [JsonStringEnumMemberName("dataset")]
    Dataset,

    [JsonStringEnumMemberName("layer")]
    Layer,

    [JsonStringEnumMemberName("published-service")]
    PublishedService,

    [JsonStringEnumMemberName("deployment")]
    Deployment,

    [JsonStringEnumMemberName("map")]
    Map,

    [JsonStringEnumMemberName("app")]
    App,

    [JsonStringEnumMemberName("result-package")]
    ResultPackage,

    [JsonStringEnumMemberName("report")]
    Report,

    [JsonStringEnumMemberName("provenance-item")]
    ProvenanceItem
}
