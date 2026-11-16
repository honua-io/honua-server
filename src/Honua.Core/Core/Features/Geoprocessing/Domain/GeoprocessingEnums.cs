// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public enum GeoprocessingWorkflowStatus
{
    Draft = 0,

    AwaitingClarification = 1,

    Validated = 2,

    AwaitingApproval = 3,

    AwaitingExecution = 8,

    Running = 4,

    Completed = 5,

    Failed = 6,

    Cancelled = 7
}

public enum GeoprocessingStageKind
{
    CaptureIntent,

    GroundCandidates,

    Clarify,

    CompilePlan,

    ValidatePlan,

    DryRun,

    Execute,

    ComposeMap,

    ComposeApp,

    Publish
}

public enum GeoprocessingStageStatus
{
    Pending,

    Completed,

    NeedsUserInput,

    Blocked,

    Failed,

    Skipped,

    Cancelled
}

public enum AnalysisPlanStepKind
{
    QueryFeatures,

    Geoprocess,

    Aggregate,

    RenderMap,

    Export
}

public enum ArtifactKind
{
    Scalar,

    FeatureLayer,

    Table,

    Raster,

    File,

    Report,

    Map,

    AppBundle
}

public enum WorkspaceKind
{
    Scratch,

    Persistent,

    TempLayer,

    SavedLayer,

    ResultCollection
}

public enum ClarificationReasonCode
{
    MissingRequiredInput,

    AmbiguousDataset,

    AmbiguousProcess,

    DestructiveAction,

    PublishAction,

    PolicyBoundary,

    LowConfidence,

    AmbiguousColumn,

    AmbiguousFilterValue,

    AmbiguousUnit,

    AmbiguousCrs,

    HeavyOperationConfirmation
}

public enum ClarificationQuestionKind
{
    SingleSelect,

    MultiSelect,

    FreeText,

    Confirmation
}

public enum AssumptionPolicy
{
    AskAlways,

    AskWhenMaterial,

    UseDefaults
}

public enum GeoprocessingErrorKind
{
    ValidationFailed,

    AuthorizationDenied,

    UnknownDataset,

    UnknownProcess,

    ExecutionFailed,

    Timeout,

    Cancelled,

    OutputBindingFailed
}
