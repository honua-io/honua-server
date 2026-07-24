// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared NoData handling for the <c>gdal_calc.py</c>-backed executors
/// (<c>raster.map-algebra</c>, <c>raster.spectral-index</c>,
/// <c>raster.reclassify</c>; #2267).
///
/// <para>
/// gdal_calc.py masks each input by its own NoData value BY DEFAULT (the
/// <c>--hideNoData</c> flag DISABLES that masking, so it is never used here).
/// What the default path does NOT do is tag the OUTPUT band with a NoData value,
/// so masked cells are written as an untagged fill and downstream consumers lose
/// the NoData contract. The fix is to resolve a concrete output NoData value —
/// an explicit caller-supplied <c>noData</c> input, otherwise the primary source
/// raster's band NoData read back via <c>gdalinfo -json</c> — and pass it as a
/// real <c>--NoDataValue</c> so gdal_calc both fills masked cells with it AND
/// records it on the output band.
/// </para>
/// </summary>
internal static class GdalNoData
{
    /// <summary>Canonical step-input name for an explicit output NoData override.</summary>
    public const string InputName = "noData";

    /// <summary>
    /// Reads the optional explicit <c>noData</c> step input. A present value must be
    /// a finite number; absent/blank reports <c>false</c> presence with no failure so
    /// the caller falls back to source-NoData detection.
    /// </summary>
    public static bool TryReadExplicitNoData(
        IReadOnlyDictionary<string, string> parameters,
        out double? value,
        out string failure)
    {
        value = null;
        failure = "";

        if (!GdalJobInputReader.TryGetInput(parameters, InputName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            failure = $"'noData' must be a finite number; got '{raw}'";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Best-effort read of the band-1 NoData value of <paramref name="inputPath"/> via
    /// <c>gdalinfo -json</c>. Detection is advisory: any failure (tool error, timeout,
    /// missing/non-numeric NoData) returns <c>null</c> so the calc job still runs — it
    /// simply runs without an output NoData tag, exactly as before this change.
    /// </summary>
    public static async Task<double?> TryReadSourceNoDataAsync(
        IGdalCommandRunner runner,
        string inputPath,
        string workspace,
        TimeSpan toolTimeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(toolTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        GdalCommandResult result;
        try
        {
            result = await runner.RunAsync("gdalinfo", ["-json", inputPath], workspace, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return result.Succeeded ? ParseBandNoData(result.StandardOutput) : null;
    }

    /// <summary>
    /// Extracts the first finite numeric <c>noDataValue</c> from a <c>gdalinfo -json</c>
    /// document's <c>bands</c> array. Returns <c>null</c> when the document is empty,
    /// unparseable, has no bands, or carries a non-numeric NoData (e.g. the string
    /// <c>"nan"</c>).
    /// </summary>
    internal static double? ParseBandNoData(string gdalinfoJson)
    {
        if (string.IsNullOrWhiteSpace(gdalinfoJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(gdalinfoJson);
            if (!document.RootElement.TryGetProperty("bands", out var bands)
                || bands.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var value = bands.EnumerateArray()
                .Select(band =>
                    band.TryGetProperty("noDataValue", out var noData) &&
                    noData.ValueKind == JsonValueKind.Number &&
                    noData.TryGetDouble(out var candidate) &&
                    double.IsFinite(candidate)
                        ? (double?)candidate
                        : null)
                .FirstOrDefault(candidate => candidate.HasValue);
            if (value.HasValue) return value.Value;

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends a <c>--NoDataValue=&lt;v&gt;</c> token when <paramref name="noData"/> has a
    /// value, formatting the number invariantly. No-ops on <c>null</c> so the calc runs
    /// with gdal_calc's default (no output NoData tag). Never emits <c>--hideNoData</c>.
    /// </summary>
    public static void AppendNoDataArg(List<string> args, double? noData)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (noData is null)
        {
            return;
        }

        args.Add($"--NoDataValue={noData.Value.ToString("R", CultureInfo.InvariantCulture)}");
    }
}
