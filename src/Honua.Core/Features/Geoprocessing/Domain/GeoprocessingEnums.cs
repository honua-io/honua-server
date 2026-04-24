// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Workflow-level lifecycle status for a geoprocessing operation.
/// </summary>
public enum GeoprocessingWorkflowStatus
{
    /// <summary>
    /// Intent captured but not yet validated.
    /// </summary>
    Draft = 0,

    /// <summary>
    /// Workflow is waiting for user clarification before proceeding.
    /// </summary>
    AwaitingClarification = 1,

    /// <summary>
    /// Intent and plan have been validated.
    /// </summary>
    Validated = 2,

    /// <summary>
    /// Plan requires explicit user approval before execution.
    /// </summary>
    AwaitingApproval = 3,

    /// <summary>
    /// Plan is validated and submitted but waiting for an execution slot.
    /// </summary>
    AwaitingExecution = 8,

    /// <summary>
    /// Execution is in progress.
    /// </summary>
    Running = 4,

    /// <summary>
    /// Execution completed successfully.
    /// </summary>
    Completed = 5,

    /// <summary>
    /// Execution failed.
    /// </summary>
    Failed = 6,

    /// <summary>
    /// Execution was cancelled by the user or system.
    /// </summary>
    Cancelled = 7
}

/// <summary>
/// Deterministic workflow stage identifiers for geoprocessing pipelines.
/// </summary>
public enum GeoprocessingStageKind
{
    /// <summary>
    /// Capture and parse the user's natural-language goal.
    /// </summary>
    CaptureIntent,

    /// <summary>
    /// Ground candidate datasets and processes against the catalog.
    /// </summary>
    GroundCandidates,

    /// <summary>
    /// Ask the user for clarification on ambiguous inputs.
    /// </summary>
    Clarify,

    /// <summary>
    /// Compile a validated analysis plan from grounded candidates.
    /// </summary>
    CompilePlan,

    /// <summary>
    /// Validate the compiled plan against constraints and policies.
    /// </summary>
    ValidatePlan,

    /// <summary>
    /// Execute a dry run to verify plan feasibility without side effects.
    /// </summary>
    DryRun,

    /// <summary>
    /// Execute the plan against real data.
    /// </summary>
    Execute,

    /// <summary>
    /// Compose a map output from execution results.
    /// </summary>
    ComposeMap,

    /// <summary>
    /// Compose an application bundle from execution results.
    /// </summary>
    ComposeApp,

    /// <summary>
    /// Publish results to a target destination.
    /// </summary>
    Publish
}

/// <summary>
/// Per-stage status within a geoprocessing workflow.
/// </summary>
public enum GeoprocessingStageStatus
{
    /// <summary>
    /// Stage has not started or is currently in progress.
    /// </summary>
    Pending,

    /// <summary>
    /// Stage completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Stage is blocked waiting for user input.
    /// </summary>
    NeedsUserInput,

    /// <summary>
    /// Stage is blocked by an upstream dependency.
    /// </summary>
    Blocked,

    /// <summary>
    /// Stage failed with an error.
    /// </summary>
    Failed,

    /// <summary>
    /// Stage was skipped (not applicable for this workflow).
    /// </summary>
    Skipped,

    /// <summary>
    /// Stage was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Categories of steps within an analysis plan.
/// </summary>
public enum AnalysisPlanStepKind
{
    /// <summary>
    /// Query features from a dataset.
    /// </summary>
    QueryFeatures,

    /// <summary>
    /// Execute a geoprocessing operation.
    /// </summary>
    Geoprocess,

    /// <summary>
    /// Aggregate or summarize feature data.
    /// </summary>
    Aggregate,

    /// <summary>
    /// Render a map from processed data.
    /// </summary>
    RenderMap,

    /// <summary>
    /// Export results to a file or external system.
    /// </summary>
    Export
}

/// <summary>
/// Categories of output artifacts produced by geoprocessing workflows.
/// </summary>
public enum ArtifactKind
{
    /// <summary>
    /// A single scalar value (count, area, distance).
    /// </summary>
    Scalar,

    /// <summary>
    /// A feature layer with geometry and attributes.
    /// </summary>
    FeatureLayer,

    /// <summary>
    /// A tabular dataset without geometry.
    /// </summary>
    Table,

    /// <summary>
    /// A raster dataset.
    /// </summary>
    Raster,

    /// <summary>
    /// A generic file output.
    /// </summary>
    File,

    /// <summary>
    /// A formatted report document.
    /// </summary>
    Report,

    /// <summary>
    /// A composed map.
    /// </summary>
    Map,

    /// <summary>
    /// A packaged application bundle.
    /// </summary>
    AppBundle
}

/// <summary>
/// Lifetime classes for managed workspaces.
/// </summary>
public enum WorkspaceKind
{
    /// <summary>
    /// Temporary scratch workspace, automatically cleaned up.
    /// </summary>
    Scratch,

    /// <summary>
    /// Persistent workspace that survives beyond the operation.
    /// </summary>
    Persistent,

    /// <summary>
    /// Temporary layer workspace for intermediate results.
    /// </summary>
    TempLayer,

    /// <summary>
    /// Saved layer workspace for published results.
    /// </summary>
    SavedLayer,

    /// <summary>
    /// Collection of result artifacts.
    /// </summary>
    ResultCollection
}

/// <summary>
/// Reason codes explaining why clarification is needed from the user.
/// </summary>
public enum ClarificationReasonCode
{
    /// <summary>
    /// A required input parameter is missing.
    /// </summary>
    MissingRequiredInput,

    /// <summary>
    /// Multiple datasets match the user's description.
    /// </summary>
    AmbiguousDataset,

    /// <summary>
    /// Multiple geoprocessing operations match the user's request.
    /// </summary>
    AmbiguousProcess,

    /// <summary>
    /// The operation would modify or delete existing data.
    /// </summary>
    DestructiveAction,

    /// <summary>
    /// The operation would publish results to a public or shared destination.
    /// </summary>
    PublishAction,

    /// <summary>
    /// The operation crosses a policy or permission boundary.
    /// </summary>
    PolicyBoundary,

    /// <summary>
    /// The system has low confidence in its interpretation of the user's intent.
    /// </summary>
    LowConfidence,

    /// <summary>
    /// Multiple columns on the resolved dataset match the user's description.
    /// </summary>
    AmbiguousColumn,

    /// <summary>
    /// A filter value cannot be resolved to a concrete literal without user input.
    /// </summary>
    AmbiguousFilterValue,

    /// <summary>
    /// A unit-carrying numeric literal (distance, duration, area) is missing an explicit unit.
    /// </summary>
    AmbiguousUnit,

    /// <summary>
    /// A CRS-sensitive operator requires an explicit projected CRS that cannot be inferred.
    /// </summary>
    AmbiguousCrs,

    /// <summary>
    /// A planned mutation introduces a heavy operation that requires explicit user confirmation.
    /// </summary>
    HeavyOperationConfirmation
}

/// <summary>
/// Interaction types for clarification questions.
/// </summary>
public enum ClarificationQuestionKind
{
    /// <summary>
    /// Select exactly one option from a list.
    /// </summary>
    SingleSelect,

    /// <summary>
    /// Select one or more options from a list.
    /// </summary>
    MultiSelect,

    /// <summary>
    /// Provide free-text input.
    /// </summary>
    FreeText,

    /// <summary>
    /// Confirm or deny a proposed action.
    /// </summary>
    Confirmation
}

/// <summary>
/// Strategy for handling assumptions during workflow compilation.
/// </summary>
public enum AssumptionPolicy
{
    /// <summary>
    /// Always ask the user before making assumptions.
    /// </summary>
    AskAlways,

    /// <summary>
    /// Ask only when the assumption materially affects results.
    /// </summary>
    AskWhenMaterial,

    /// <summary>
    /// Use sensible defaults without asking.
    /// </summary>
    UseDefaults
}

/// <summary>
/// Categories of errors that can occur during geoprocessing.
/// </summary>
public enum GeoprocessingErrorKind
{
    /// <summary>
    /// Input validation failed.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// The user is not authorized to perform the requested operation.
    /// </summary>
    AuthorizationDenied,

    /// <summary>
    /// A referenced dataset could not be found.
    /// </summary>
    UnknownDataset,

    /// <summary>
    /// A referenced geoprocessing operation could not be found.
    /// </summary>
    UnknownProcess,

    /// <summary>
    /// An error occurred during plan execution.
    /// </summary>
    ExecutionFailed,

    /// <summary>
    /// The operation exceeded its time limit.
    /// </summary>
    Timeout,

    /// <summary>
    /// The operation was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// An output could not be bound to its target destination.
    /// </summary>
    OutputBindingFailed
}
