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

            foreach (var band in bands.EnumerateArray())
            {
                if (band.TryGetProperty("noDataValue", out var noData)
                    && TryParseNoDataElement(noData, out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string FormatCalcLiteral(double value, bool qualifiedNumpy)
    {
        var prefix = qualifiedNumpy ? "numpy." : string.Empty;
        if (double.IsNaN(value))
        {
            return $"{prefix}nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return $"{prefix}inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return $"-{prefix}inf";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryParseNoDataElement(JsonElement element, out double value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var trimmed = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (string.Equals(trimmed, "NaN", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NaN;
            return true;
        }

        if (string.Equals(trimmed, "Infinity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "+Infinity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "inf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "+inf", StringComparison.OrdinalIgnoreCase))
        {
            value = double.PositiveInfinity;
            return true;
        }

        if (string.Equals(trimmed, "-Infinity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "-inf", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NegativeInfinity;
            return true;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
