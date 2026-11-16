// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalNoData
{
    public const string InputName = "noData";

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

    public static async Task<double?> TryReadSourceNoDataAsync(
        IGdalCommandRunner runner,
        string inputPath,
        string workspace,
        TimeSpan toolTimeout,
        CancellationToken cancellationToken,
        string? expectedETag = null,
        GdalWorkerOptions? options = null)
    {
        using var timeoutCts = new CancellationTokenSource(toolTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        GdalCommandResult result;
        try
        {
            var args = new List<string> { "-json" };
            GdalRasterInput.AddReadPin(args, expectedETag);
            args.Add(inputPath);
            IReadOnlyList<string> commandArgs = options is null
                ? args
                : GdalRasterDriverAllowlist.Prefix("gdalinfo", args, options);
            result = await runner.RunAsync("gdalinfo", commandArgs, workspace, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return result.Succeeded ? ParseBandNoData(result.StandardOutput) : null;
    }

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
