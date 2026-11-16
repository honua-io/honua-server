// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Studio.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageFamily>))]
public enum StudioPackageFamily
{
    [JsonStringEnumMemberName("query")]
    Query,

    [JsonStringEnumMemberName("analysis")]
    Analysis,

    [JsonStringEnumMemberName("map")]
    Map,

    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    [JsonStringEnumMemberName("report")]
    Report,

    [JsonStringEnumMemberName("form")]
    Form,

    [JsonStringEnumMemberName("app")]
    App,

    [JsonStringEnumMemberName("workflow")]
    Workflow,

    [JsonStringEnumMemberName("gp")]
    Geoprocessing,

    [JsonStringEnumMemberName("etl")]
    Etl,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageOperation>))]
public enum StudioPackageOperation
{
    [JsonStringEnumMemberName("draft.create")]
    DraftCreate,

    [JsonStringEnumMemberName("draft.read")]
    DraftRead,

    [JsonStringEnumMemberName("draft.update")]
    DraftUpdate,

    [JsonStringEnumMemberName("validate")]
    Validate,

    [JsonStringEnumMemberName("preview-plan")]
    PreviewPlan,

    [JsonStringEnumMemberName("content-version.create")]
    ContentVersionCreate,

    [JsonStringEnumMemberName("content-version.read")]
    ContentVersionRead,

    [JsonStringEnumMemberName("content-version.compare")]
    ContentVersionCompare,

    [JsonStringEnumMemberName("publish-request.create")]
    PublishRequestCreate,

    [JsonStringEnumMemberName("reopen")]
    Reopen,

    [JsonStringEnumMemberName("rollback")]
    Rollback,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageSupportLevel>))]
public enum StudioPackageSupportLevel
{
    [JsonStringEnumMemberName("unsupported")]
    Unsupported,

    [JsonStringEnumMemberName("limited")]
    Limited,

    [JsonStringEnumMemberName("supported")]
    Supported,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioPackagePersistenceMode>))]
public enum StudioPackagePersistenceMode
{
    [JsonStringEnumMemberName("in-memory")]
    InMemory,

    [JsonStringEnumMemberName("durable")]
    Durable,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageValidationStatus>))]
public enum StudioPackageValidationStatus
{
    [JsonStringEnumMemberName("not-validated")]
    NotValidated,

    [JsonStringEnumMemberName("valid")]
    Valid,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("invalid")]
    Invalid,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageDiagnosticSeverity>))]
public enum StudioPackageDiagnosticSeverity
{
    [JsonStringEnumMemberName("info")]
    Info,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("blocker")]
    Blocker,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioPublicationRequestStatus>))]
public enum StudioPublicationRequestStatus
{
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("rejected")]
    Rejected,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioRollbackPointer>))]
public enum StudioRollbackPointer
{
    [JsonStringEnumMemberName("current")]
    Current,

    [JsonStringEnumMemberName("published")]
    Published,

    [JsonStringEnumMemberName("both")]
    Both,
}

[JsonConverter(typeof(JsonStringEnumConverter<StudioContentItemState>))]
public enum StudioContentItemState
{
    [JsonStringEnumMemberName("draft")]
    Draft,

    [JsonStringEnumMemberName("current")]
    Current,

    [JsonStringEnumMemberName("published")]
    Published,
}
