// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>
/// Runtime families that can contribute nodes to the workflow package registry.
/// </summary>
public enum WorkflowNodeRuntimeKind
{
    /// <summary>
    /// Built-in geoprocessing process catalog node.
    /// </summary>
    Geoprocessing,

    /// <summary>
    /// Extract-transform-load node. Execution support is provider-dependent.
    /// </summary>
    ExtractTransformLoad,

    /// <summary>
    /// Workflow-only utility node such as control flow or failure handling.
    /// </summary>
    WorkflowUtility
}

/// <summary>
/// Subset of JSON-schema value types exposed to Console clients.
/// </summary>
public enum WorkflowSchemaValueType
{
    /// <summary>
    /// JSON string value.
    /// </summary>
    Text,

    /// <summary>
    /// JSON integer value.
    /// </summary>
    WholeNumber,

    /// <summary>
    /// JSON number value.
    /// </summary>
    DecimalNumber,

    /// <summary>
    /// JSON boolean value.
    /// </summary>
    Flag,

    /// <summary>
    /// JSON array value.
    /// </summary>
    List,

    /// <summary>
    /// JSON object value.
    /// </summary>
    Structured
}

/// <summary>
/// Edge semantics between workflow package nodes.
/// </summary>
public enum WorkflowEdgeKind
{
    /// <summary>
    /// Data produced by the source node is bound to the target node.
    /// </summary>
    Data,

    /// <summary>
    /// Control dependency without data binding.
    /// </summary>
    Control,

    /// <summary>
    /// Failure branch evaluated when the source node fails.
    /// </summary>
    Failure
}

/// <summary>
/// Publication target for a workflow package version.
/// </summary>
public enum WorkflowPublicationTarget
{
    /// <summary>
    /// Package version can be run as a durable execution job template.
    /// </summary>
    Job,

    /// <summary>
    /// Package version is bound to a schedule.
    /// </summary>
    Schedule,

    /// <summary>
    /// Package version is exposed through an eligible process-style endpoint.
    /// </summary>
    ProcessEndpoint
}

/// <summary>
/// Lifecycle state of a workflow package publication.
/// </summary>
public enum WorkflowPublicationStatus
{
    /// <summary>
    /// Publication is active and may be used for new runs.
    /// </summary>
    Active,

    /// <summary>
    /// Publication is disabled and cannot create new runs.
    /// </summary>
    Disabled
}
