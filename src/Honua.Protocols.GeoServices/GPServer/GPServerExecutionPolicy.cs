// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Classifies which catalog tasks are eligible for the GeoServices synchronous
/// <c>execute</c> contract, and maps that classification onto the Esri
/// <c>executionType</c> wire string.
///
/// Esri GP clients pick the request verb (<c>execute</c> vs
/// <c>submitJob</c>) from the task's <c>executionType</c>. Honua exposes the
/// deterministic single-geometry <c>geometry.*</c> family — which runs inline
/// on the local backend in bounded time — as
/// <c>esriExecutionTypeSynchronous</c>; everything else (heavyweight raster /
/// surface, layer-scoped analytics, destructive data-management) remains
/// <c>esriExecutionTypeAsynchronous</c>.
/// </summary>
internal static class GPServerExecutionPolicy
{
    /// <summary>Esri synchronous execution-type wire string.</summary>
    public const string SynchronousExecutionType = "esriExecutionTypeSynchronous";

    /// <summary>Esri asynchronous execution-type wire string.</summary>
    public const string AsynchronousExecutionType = "esriExecutionTypeAsynchronous";

    /// <summary>Returns <c>true</c> when the task may use the shared job runtime.</summary>
    public static bool IsJobCallable(ProcessDefinition definition)
        => ProcessExecutionEligibility.IsJobCallable(definition);

    /// <summary>
    /// Returns <c>true</c> when the task is eligible for synchronous execution.
    /// </summary>
    public static bool IsSynchronous(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return IsJobCallable(definition)
            && (definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0;
    }

    /// <summary>
    /// Returns the Esri <c>executionType</c> wire string for the task.
    /// </summary>
    public static string ExecutionType(ProcessDefinition definition)
        => IsSynchronous(definition) ? SynchronousExecutionType : AsynchronousExecutionType;
}
