// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// A self-describing per-process geoprocessing executor. Extends <see cref="IJobExecutor"/>
/// with a self-declared set of process ids the executor handles, enabling the
/// geoprocessing/GDAL dispatchers to auto-register and route by <c>processId</c>
/// without a hand-maintained constructor that names every executor explicitly
/// (the GP Devkit authoring contract, issue #2122).
/// </summary>
/// <remarks>
/// An instance property (rather than a <c>[GpProcess]</c> attribute) is used so
/// that executors which fan a single implementation out over several process ids
/// — for example the GDAL surface and raster-statistics families — can compute
/// their id set at construction time, and so the dispatcher can build its routing
/// table from DI-resolved instances with no reflection (AOT-friendly). Each id in
/// <see cref="ProcessIds"/> must be unique across the registered executor set;
/// the dispatcher fails fast on a duplicate or empty declaration.
/// </remarks>
public interface IProcessExecutor : IJobExecutor
{
    /// <summary>
    /// The non-empty set of canonical dotted process ids this executor handles
    /// (e.g. <c>geometry.buffer</c>, or the several <c>surface.*</c> ids a single
    /// GDAL surface executor serves). Comparison is ordinal. Must contain at least
    /// one id; ids must not be null, empty, or whitespace.
    /// </summary>
    IReadOnlySet<string> ProcessIds { get; }
}
