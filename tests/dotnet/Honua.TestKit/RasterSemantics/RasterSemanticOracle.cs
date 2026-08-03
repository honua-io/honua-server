// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.TestKit.RasterSemantics;

/// <summary>Terminal outcome covered by a raster semantic fixture.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RasterSemanticOutcome>))]
public enum RasterSemanticOutcome
{
    /// <summary>The process produces a comparable raster or scalar result.</summary>
    Success,

    /// <summary>The process rejects the input with the fixture's stable error code.</summary>
    Error,

    /// <summary>The process observes cancellation without publishing a partial result.</summary>
    Cancelled,
}

/// <summary>Affine grid metadata compared independently from encoded raster bytes.</summary>
public sealed record RasterSemanticGrid
{
    /// <summary>Pixel columns.</summary>
    public required int Width { get; init; }

    /// <summary>Pixel rows.</summary>
    public required int Height { get; init; }

    /// <summary>EPSG spatial reference identifier, or zero for deliberately unknown CRS.</summary>
    public required int Srid { get; init; }

    /// <summary>Six coefficients of the GDAL/PostGIS affine transform.</summary>
    public required IReadOnlyList<double> Transform { get; init; }
}

/// <summary>Provider-neutral raster band values in row-major order.</summary>
public sealed record RasterSemanticBand
{
    /// <summary>Canonical pixel type, such as <c>8BUI</c> or <c>32BF</c>.</summary>
    public required string PixelType { get; init; }

    /// <summary>Canonical color interpretation, such as <c>gray</c>, <c>red</c>, or <c>undefined</c>.</summary>
    public required string ColorInterpretation { get; init; }

    /// <summary>NoData marker; <see langword="null"/> means the band has no declared marker.</summary>
    public double? NoData { get; init; }

    /// <summary>
    /// Row-major cells. A <see langword="null"/> cell is semantic NoData, independent of the
    /// encoded marker used by a particular provider.
    /// </summary>
    public required IReadOnlyList<double?> Cells { get; init; }
}

/// <summary>Decoded provider-neutral result used by the semantic oracle.</summary>
public sealed record RasterSemanticSnapshot
{
    /// <summary>Raster grid when the result is raster-shaped.</summary>
    public RasterSemanticGrid? Grid { get; init; }

    /// <summary>Decoded raster bands; empty for scalar-only results.</summary>
    public IReadOnlyList<RasterSemanticBand> Bands { get; init; } = [];

    /// <summary>Named scalar outputs such as statistics or histogram bins.</summary>
    public IReadOnlyDictionary<string, double?> Scalars { get; init; } =
        new ReadOnlyDictionary<string, double?>(new Dictionary<string, double?>(StringComparer.Ordinal));
}

/// <summary>Deliberately narrow numeric tolerances for one semantic fixture.</summary>
public sealed record RasterSemanticTolerance
{
    /// <summary>Absolute tolerance for affine transform coefficients.</summary>
    public double GridAbsolute { get; init; }

    /// <summary>Absolute tolerance for decoded cell values.</summary>
    public double CellAbsolute { get; init; }

    /// <summary>Relative tolerance for decoded cell values.</summary>
    public double CellRelative { get; init; }

    /// <summary>Absolute tolerance for scalar values.</summary>
    public double ScalarAbsolute { get; init; }

    /// <summary>Relative tolerance for scalar values.</summary>
    public double ScalarRelative { get; init; }
}

/// <summary>One checked-in semantic fixture shared by PostGIS and native GDAL runners.</summary>
public sealed record RasterSemanticFixture
{
    /// <summary>Stable evidence identifier advertised by engine capability metadata.</summary>
    public required string Id { get; init; }

    /// <summary>Canonical raster process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Engine-independent semantic contract version.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Specific algorithm/parameter variant exercised by the fixture.</summary>
    public required string Variant { get; init; }

    /// <summary>Semantic dimensions proved by the fixture.</summary>
    public required IReadOnlyList<string> Coverage { get; init; }

    /// <summary>Expected terminal outcome.</summary>
    public required RasterSemanticOutcome Outcome { get; init; }

    /// <summary>Stable error code for <see cref="RasterSemanticOutcome.Error"/>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Expected decoded result for <see cref="RasterSemanticOutcome.Success"/>.</summary>
    public RasterSemanticSnapshot? Expected { get; init; }

    /// <summary>Per-fixture comparison tolerances.</summary>
    public required RasterSemanticTolerance Tolerance { get; init; }
}

/// <summary>Version-stamped observation emitted by one engine's fixture runner.</summary>
public sealed record RasterSemanticObservation
{
    /// <summary>Canonical raster process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Engine-independent semantic contract version.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Provider-neutral engine name.</summary>
    public required string Engine { get; init; }

    /// <summary>Honua implementation identifier and version.</summary>
    public required string ImplementationVersion { get; init; }

    /// <summary>Observed upstream runtime version.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Observed terminal outcome.</summary>
    public required RasterSemanticOutcome Outcome { get; init; }

    /// <summary>Stable error code for an error outcome.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Decoded result for a successful outcome.</summary>
    public RasterSemanticSnapshot? Snapshot { get; init; }
}

/// <summary>One deterministic semantic difference.</summary>
public sealed record RasterSemanticDifference(string Path, string Message);

/// <summary>Bounded comparison result returned by <see cref="RasterSemanticOracle"/>.</summary>
public sealed record RasterSemanticComparison
{
    /// <summary>Whether no semantic difference was observed.</summary>
    public bool IsMatch => Differences.Count == 0 && OmittedDifferenceCount == 0;

    /// <summary>First bounded set of deterministic differences.</summary>
    public required IReadOnlyList<RasterSemanticDifference> Differences { get; init; }

    /// <summary>Additional differences omitted after reaching the diagnostic ceiling.</summary>
    public required int OmittedDifferenceCount { get; init; }
}

/// <summary>
/// Compares decoded raster semantics rather than format bytes. The oracle is intentionally strict
/// about grid, NoData topology, pixel type, color interpretation, and scalar names; only finite
/// numeric values receive the fixture's explicit absolute/relative tolerances.
/// </summary>
public static class RasterSemanticOracle
{
    private const int MaximumDifferences = 100;
    private const int MaximumBands = 64;
    private const int MaximumCells = 1_048_576;
    private const int MaximumScalars = 16_384;

    /// <summary>
    /// Compares a version-stamped engine observation to a checked-in fixture, including terminal
    /// behavior and the prohibition on partial result publication after error or cancellation.
    /// </summary>
    public static RasterSemanticComparison Compare(
        RasterSemanticFixture fixture,
        RasterSemanticObservation observation)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.Engine)
            || string.IsNullOrWhiteSpace(observation.ImplementationVersion)
            || string.IsNullOrWhiteSpace(observation.RuntimeVersion))
        {
            throw new ArgumentException(
                "Raster semantic observations require engine, implementation, and runtime versions.",
                nameof(observation));
        }

        var differences = new List<RasterSemanticDifference>();
        AddExactDifference("processId", fixture.ProcessId, observation.ProcessId, differences);
        AddExactDifference("semanticVersion", fixture.SemanticVersion, observation.SemanticVersion, differences);
        AddExactDifference("outcome", fixture.Outcome, observation.Outcome, differences);
        AddExactDifference("errorCode", fixture.ErrorCode, observation.ErrorCode, differences);

        if (fixture.Outcome == RasterSemanticOutcome.Success)
        {
            if (observation.Snapshot is null)
            {
                differences.Add(new RasterSemanticDifference("snapshot", "Successful observation has no result."));
            }
            else if (fixture.Expected is { } expected)
            {
                var snapshotComparison = Compare(expected, observation.Snapshot, fixture.Tolerance);
                differences.AddRange(snapshotComparison.Differences);
                return Bound(differences, snapshotComparison.OmittedDifferenceCount);
            }
        }
        else if (observation.Snapshot is not null)
        {
            differences.Add(new RasterSemanticDifference(
                "snapshot",
                "Error/cancelled observation published a partial semantic result."));
        }

        return Bound(differences, omittedCount: 0);
    }

    /// <summary>Compares an expected semantic snapshot with one decoded from an engine result.</summary>
    public static RasterSemanticComparison Compare(
        RasterSemanticSnapshot expected,
        RasterSemanticSnapshot actual,
        RasterSemanticTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerance);
        ValidateTolerance(tolerance);
        ValidateSnapshot(expected, nameof(expected));
        ValidateSnapshot(actual, nameof(actual));

        var differences = new DifferenceCollector(MaximumDifferences);
        CompareGrid(expected.Grid, actual.Grid, tolerance.GridAbsolute, differences);
        CompareBands(expected.Bands, actual.Bands, tolerance, differences);
        CompareScalars(expected.Scalars, actual.Scalars, tolerance, differences);

        return new RasterSemanticComparison
        {
            Differences = Array.AsReadOnly(differences.Items.ToArray()),
            OmittedDifferenceCount = differences.OmittedCount,
        };
    }

    private static void CompareGrid(
        RasterSemanticGrid? expected,
        RasterSemanticGrid? actual,
        double tolerance,
        DifferenceCollector differences)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                differences.Add("grid", "Expected and actual grid presence differ.");
            }

            return;
        }

        CompareExact("grid.width", expected.Width, actual.Width, differences);
        CompareExact("grid.height", expected.Height, actual.Height, differences);
        CompareExact("grid.srid", expected.Srid, actual.Srid, differences);
        for (var index = 0; index < expected.Transform.Count; index++)
        {
            CompareNumber(
                $"grid.transform[{index}]",
                expected.Transform[index],
                actual.Transform[index],
                tolerance,
                relativeTolerance: 0,
                differences);
        }
    }

    private static void CompareBands(
        IReadOnlyList<RasterSemanticBand> expected,
        IReadOnlyList<RasterSemanticBand> actual,
        RasterSemanticTolerance tolerance,
        DifferenceCollector differences)
    {
        CompareExact("bands.count", expected.Count, actual.Count, differences);
        var bandCount = Math.Min(expected.Count, actual.Count);
        for (var bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            var expectedBand = expected[bandIndex];
            var actualBand = actual[bandIndex];
            var prefix = $"bands[{bandIndex}]";
            CompareExact($"{prefix}.pixelType", expectedBand.PixelType, actualBand.PixelType, differences);
            CompareExact(
                $"{prefix}.colorInterpretation",
                expectedBand.ColorInterpretation,
                actualBand.ColorInterpretation,
                differences);
            CompareNullableNumber(
                $"{prefix}.noData",
                expectedBand.NoData,
                actualBand.NoData,
                absoluteTolerance: 0,
                relativeTolerance: 0,
                differences);
            CompareExact($"{prefix}.cells.count", expectedBand.Cells.Count, actualBand.Cells.Count, differences);

            var cellCount = Math.Min(expectedBand.Cells.Count, actualBand.Cells.Count);
            for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                CompareNullableNumber(
                    $"{prefix}.cells[{cellIndex}]",
                    expectedBand.Cells[cellIndex],
                    actualBand.Cells[cellIndex],
                    tolerance.CellAbsolute,
                    tolerance.CellRelative,
                    differences);
            }
        }
    }

    private static void CompareScalars(
        IReadOnlyDictionary<string, double?> expected,
        IReadOnlyDictionary<string, double?> actual,
        RasterSemanticTolerance tolerance,
        DifferenceCollector differences)
    {
        foreach (var key in expected.Keys.Order(StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(key, out var actualValue))
            {
                differences.Add($"scalars.{key}", "Expected scalar is missing.");
                continue;
            }

            CompareNullableNumber(
                $"scalars.{key}",
                expected[key],
                actualValue,
                tolerance.ScalarAbsolute,
                tolerance.ScalarRelative,
                differences);
        }

        foreach (var key in actual.Keys.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            differences.Add($"scalars.{key}", "Unexpected scalar is present.");
        }
    }

    private static void CompareNullableNumber(
        string path,
        double? expected,
        double? actual,
        double absoluteTolerance,
        double relativeTolerance,
        DifferenceCollector differences)
    {
        if (expected is null || actual is null)
        {
            if (expected != actual)
            {
                differences.Add(path, "NoData/null topology differs.");
            }

            return;
        }

        CompareNumber(path, expected.Value, actual.Value, absoluteTolerance, relativeTolerance, differences);
    }

    private static void CompareNumber(
        string path,
        double expected,
        double actual,
        double absoluteTolerance,
        double relativeTolerance,
        DifferenceCollector differences)
    {
        if (!double.IsFinite(expected) || !double.IsFinite(actual))
        {
            differences.Add(path, "Non-finite values are not valid semantic evidence.");
            return;
        }

        var delta = Math.Abs(expected - actual);
        var allowed = Math.Max(absoluteTolerance, relativeTolerance * Math.Max(Math.Abs(expected), Math.Abs(actual)));
        if (delta > allowed)
        {
            differences.Add(
                path,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected {expected:R}, actual {actual:R}, delta {delta:R}, allowed {allowed:R}."));
        }
    }

    private static void CompareExact<T>(string path, T expected, T actual, DifferenceCollector differences)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            differences.Add(path, $"Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void AddExactDifference<T>(
        string path,
        T expected,
        T actual,
        List<RasterSemanticDifference> differences)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            differences.Add(new RasterSemanticDifference(path, $"Expected '{expected}', actual '{actual}'."));
        }
    }

    private static RasterSemanticComparison Bound(
        List<RasterSemanticDifference> differences,
        int omittedCount)
    {
        var visibleCount = Math.Min(differences.Count, MaximumDifferences);
        return new RasterSemanticComparison
        {
            Differences = Array.AsReadOnly(differences.Take(visibleCount).ToArray()),
            OmittedDifferenceCount = omittedCount + Math.Max(0, differences.Count - visibleCount),
        };
    }

    private static void ValidateSnapshot(RasterSemanticSnapshot snapshot, string parameterName)
    {
        if (snapshot.Grid is { } grid)
        {
            if (grid.Width <= 0 || grid.Height <= 0 || grid.Transform is null || grid.Transform.Count != 6)
            {
                throw new ArgumentException("Raster semantic grids require positive dimensions and six transform coefficients.", parameterName);
            }

            if (grid.Transform.Any(value => !double.IsFinite(value)))
            {
                throw new ArgumentException("Raster semantic grid coefficients must be finite.", parameterName);
            }
        }

        if (snapshot.Bands is null || snapshot.Scalars is null || snapshot.Bands.Count > MaximumBands)
        {
            throw new ArgumentException("Raster semantic snapshot collections are missing or exceed their bounds.", parameterName);
        }

        var expectedCells = snapshot.Grid is null
            ? (long?)null
            : checked((long)snapshot.Grid.Width * snapshot.Grid.Height);
        foreach (var band in snapshot.Bands)
        {
            if (band is null
                || string.IsNullOrWhiteSpace(band.PixelType)
                || string.IsNullOrWhiteSpace(band.ColorInterpretation)
                || band.Cells is null
                || band.Cells.Count > MaximumCells
                || (expectedCells is not null && band.Cells.Count != expectedCells))
            {
                throw new ArgumentException("Raster semantic band metadata or cell dimensions are invalid.", parameterName);
            }

            if (band.NoData is { } noData && !double.IsFinite(noData)
                || band.Cells.Any(value => value is { } present && !double.IsFinite(present)))
            {
                throw new ArgumentException("Raster semantic band values must be finite or null NoData cells.", parameterName);
            }
        }

        if (snapshot.Scalars.Count > MaximumScalars
            || snapshot.Scalars.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Value is { } present && !double.IsFinite(present)))
        {
            throw new ArgumentException("Raster semantic scalar values are invalid or exceed their bounds.", parameterName);
        }
    }

    private static void ValidateTolerance(RasterSemanticTolerance tolerance)
    {
        var values = new[]
        {
            tolerance.GridAbsolute,
            tolerance.CellAbsolute,
            tolerance.CellRelative,
            tolerance.ScalarAbsolute,
            tolerance.ScalarRelative,
        };
        if (values.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Raster semantic tolerances must be finite and non-negative.");
        }
    }

    private sealed class DifferenceCollector(int maximum)
    {
        public List<RasterSemanticDifference> Items { get; } = new(maximum);

        public int OmittedCount { get; private set; }

        public void Add(string path, string message)
        {
            if (Items.Count < maximum)
            {
                Items.Add(new RasterSemanticDifference(path, message));
            }
            else
            {
                OmittedCount++;
            }
        }
    }
}

/// <summary>Loads and validates the checked-in cross-engine fixture manifest.</summary>
public static class RasterSemanticFixtureCatalog
{
    private const string ResourceSuffix = ".RasterSemantics.Fixtures.raster-semantic-fixtures.json";

    /// <summary>Loads the embedded fixture manifest.</summary>
    public static IReadOnlyList<RasterSemanticFixture> Load()
    {
        var assembly = typeof(RasterSemanticFixtureCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The raster semantic fixture manifest is not embedded.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The raster semantic fixture manifest could not be opened.");
        var fixtures = JsonSerializer.Deserialize<List<RasterSemanticFixture>>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }) ?? throw new InvalidOperationException("The raster semantic fixture manifest is empty.");

        Validate(fixtures);
        return Array.AsReadOnly(fixtures.ToArray());
    }

    private static void Validate(List<RasterSemanticFixture> fixtures)
    {
        if (fixtures.Count == 0)
        {
            throw new InvalidOperationException("At least one raster semantic fixture is required.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in fixtures)
        {
            if (fixture is null
                || string.IsNullOrWhiteSpace(fixture.Id)
                || string.IsNullOrWhiteSpace(fixture.ProcessId)
                || string.IsNullOrWhiteSpace(fixture.SemanticVersion)
                || string.IsNullOrWhiteSpace(fixture.Variant)
                || fixture.Coverage is null
                || fixture.Coverage.Count == 0
                || fixture.Coverage.Any(string.IsNullOrWhiteSpace)
                || fixture.Tolerance is null
                || !ids.Add(fixture.Id))
            {
                throw new InvalidOperationException("Raster semantic fixture metadata is incomplete or duplicated.");
            }

            var expectedShape = fixture.Outcome == RasterSemanticOutcome.Success;
            if (expectedShape != (fixture.Expected is not null)
                || (fixture.Outcome == RasterSemanticOutcome.Error) != !string.IsNullOrWhiteSpace(fixture.ErrorCode)
                || fixture.Outcome == RasterSemanticOutcome.Cancelled && fixture.ErrorCode is not null)
            {
                throw new InvalidOperationException($"Raster semantic fixture '{fixture.Id}' has an invalid terminal outcome contract.");
            }

            if (fixture.Expected is { } expected)
            {
                _ = RasterSemanticOracle.Compare(expected, expected, fixture.Tolerance);
            }
        }
    }
}
