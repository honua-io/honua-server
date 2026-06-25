// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// A single golden GP test case (GP Devkit P6, issue #2127): the process to run, the
/// step-0 inputs to feed it, the comparator + tolerance to apply, and the on-disk golden
/// file its published artifact is asserted against. Authored as a JSON manifest under
/// <c>samples/gp/</c> (see <see cref="GoldenFixtureLoader"/>) or constructed directly in a
/// hand-written xUnit test.
/// </summary>
public sealed class GoldenFixture
{
    /// <summary>
    /// Builds a fixture.
    /// </summary>
    /// <param name="id">Stable fixture id (defaults the golden file name; used by <c>gp test &lt;id&gt;</c>).</param>
    /// <param name="processId">Canonical dotted process id to run (e.g. <c>geometry.buffer</c>).</param>
    /// <param name="inputs">Step-0 input name/value pairs fed to the process.</param>
    /// <param name="goldenPath">Absolute path to the golden file the artifact is asserted against.</param>
    /// <param name="mode">The comparator to apply.</param>
    /// <param name="tolerance">The coordinate/numeric tolerances.</param>
    /// <param name="description">Optional human-readable description.</param>
    public GoldenFixture(
        string id,
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        string goldenPath,
        GoldenComparisonMode mode = GoldenComparisonMode.Auto,
        GoldenTolerance? tolerance = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(goldenPath);

        Id = id;
        ProcessId = processId;
        Inputs = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(inputs, StringComparer.Ordinal));
        GoldenPath = goldenPath;
        Mode = mode;
        Tolerance = tolerance ?? GoldenTolerance.Default;
        Description = description;
    }

    /// <summary>Stable fixture id.</summary>
    public string Id { get; }

    /// <summary>Process id to run.</summary>
    public string ProcessId { get; }

    /// <summary>Step-0 inputs fed to the process.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; }

    /// <summary>Absolute path to the golden file.</summary>
    public string GoldenPath { get; }

    /// <summary>The comparator to apply.</summary>
    public GoldenComparisonMode Mode { get; }

    /// <summary>The tolerances to apply.</summary>
    public GoldenTolerance Tolerance { get; }

    /// <summary>Optional description.</summary>
    public string? Description { get; }
}
