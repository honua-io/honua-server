// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Studio.Domain;

/// <summary>
/// Studio package families supported by the shared package lifecycle.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageFamily>))]
public enum StudioPackageFamily
{
    /// <summary>Saved query package.</summary>
    [JsonStringEnumMemberName("query")]
    Query,

    /// <summary>Analysis package.</summary>
    [JsonStringEnumMemberName("analysis")]
    Analysis,

    /// <summary>Map package.</summary>
    [JsonStringEnumMemberName("map")]
    Map,

    /// <summary>Dashboard package.</summary>
    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    /// <summary>Report package.</summary>
    [JsonStringEnumMemberName("report")]
    Report,

    /// <summary>Form package.</summary>
    [JsonStringEnumMemberName("form")]
    Form,

    /// <summary>Generated application package.</summary>
    [JsonStringEnumMemberName("app")]
    App,

    /// <summary>Workflow package.</summary>
    [JsonStringEnumMemberName("workflow")]
    Workflow,

    /// <summary>Geoprocessing package.</summary>
    [JsonStringEnumMemberName("gp")]
    Geoprocessing,

    /// <summary>Extract-transform-load package.</summary>
    [JsonStringEnumMemberName("etl")]
    Etl,
}

/// <summary>
/// Lifecycle operations that can be advertised per Studio package family.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageOperation>))]
public enum StudioPackageOperation
{
    /// <summary>Create a mutable draft.</summary>
    [JsonStringEnumMemberName("draft.create")]
    DraftCreate,

    /// <summary>Read a mutable draft.</summary>
    [JsonStringEnumMemberName("draft.read")]
    DraftRead,

    /// <summary>Update a mutable draft.</summary>
    [JsonStringEnumMemberName("draft.update")]
    DraftUpdate,

    /// <summary>Validate package content.</summary>
    [JsonStringEnumMemberName("validate")]
    Validate,

    /// <summary>Generate a preview plan.</summary>
    [JsonStringEnumMemberName("preview-plan")]
    PreviewPlan,

    /// <summary>Save immutable content versions.</summary>
    [JsonStringEnumMemberName("content-version.create")]
    ContentVersionCreate,

    /// <summary>Read immutable content versions.</summary>
    [JsonStringEnumMemberName("content-version.read")]
    ContentVersionRead,

    /// <summary>Compare immutable content versions.</summary>
    [JsonStringEnumMemberName("content-version.compare")]
    ContentVersionCompare,

    /// <summary>Create publication requests.</summary>
    [JsonStringEnumMemberName("publish-request.create")]
    PublishRequestCreate,

    /// <summary>Reopen an immutable version as a new mutable draft.</summary>
    [JsonStringEnumMemberName("reopen")]
    Reopen,

    /// <summary>Rollback current or published pointers to an earlier version.</summary>
    [JsonStringEnumMemberName("rollback")]
    Rollback,
}

/// <summary>
/// Advertised support level for a Studio package family.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageSupportLevel>))]
public enum StudioPackageSupportLevel
{
    /// <summary>The family is unavailable in this deployment context.</summary>
    [JsonStringEnumMemberName("unsupported")]
    Unsupported,

    /// <summary>The lifecycle is available with envelope validation only.</summary>
    [JsonStringEnumMemberName("limited")]
    Limited,

    /// <summary>The lifecycle and family-specific validation are available.</summary>
    [JsonStringEnumMemberName("supported")]
    Supported,
}

/// <summary>
/// Persistence mode backing the Studio package lifecycle.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioPackagePersistenceMode>))]
public enum StudioPackagePersistenceMode
{
    /// <summary>Package data is persisted only in process memory.</summary>
    [JsonStringEnumMemberName("in-memory")]
    InMemory,

    /// <summary>Package data is persisted in durable storage.</summary>
    [JsonStringEnumMemberName("durable")]
    Durable,
}

/// <summary>
/// Validation status for package drafts and immutable content versions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageValidationStatus>))]
public enum StudioPackageValidationStatus
{
    /// <summary>The package has not been validated.</summary>
    [JsonStringEnumMemberName("not-validated")]
    NotValidated,

    /// <summary>The package is valid.</summary>
    [JsonStringEnumMemberName("valid")]
    Valid,

    /// <summary>The package is valid but includes warnings.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>The package is invalid.</summary>
    [JsonStringEnumMemberName("invalid")]
    Invalid,
}

/// <summary>
/// Severity for one Studio validation diagnostic.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioPackageDiagnosticSeverity>))]
public enum StudioPackageDiagnosticSeverity
{
    /// <summary>Informational diagnostic.</summary>
    [JsonStringEnumMemberName("info")]
    Info,

    /// <summary>Warning diagnostic.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>Error diagnostic.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>Blocking diagnostic.</summary>
    [JsonStringEnumMemberName("blocker")]
    Blocker,
}

/// <summary>
/// Current state of a persisted Studio publication request.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioPublicationRequestStatus>))]
public enum StudioPublicationRequestStatus
{
    /// <summary>The request has been accepted and persisted.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    /// <summary>The request is waiting for asynchronous publication execution.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The request was rejected before execution.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,
}

/// <summary>
/// Pointer updated by a Studio rollback request.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StudioRollbackPointer>))]
public enum StudioRollbackPointer
{
    /// <summary>Update only the current editing pointer.</summary>
    [JsonStringEnumMemberName("current")]
    Current,

    /// <summary>Update only the published pointer.</summary>
    [JsonStringEnumMemberName("published")]
    Published,

    /// <summary>Update both current and published pointers.</summary>
    [JsonStringEnumMemberName("both")]
    Both,
}
