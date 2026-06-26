// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.CustomCode.Sdk;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace Honua.CustomCode.Samples;

/// <summary>
/// Sample custom-code RASTER GP tool: process a GeoTIFF in-process with the GDAL C#
/// bindings (<c>OSGeo.GDAL</c>/<c>OSGeo.OGR</c>/<c>OSGeo.OSR</c>). This is the raster
/// counterpart of <see cref="BufferTool"/> (vector) and the .NET mirror of the Python
/// <c>raster_ndvi_tool.py</c> sample — it proves first-class, in-process GDAL interop in
/// the .NET runtime.
/// </summary>
/// <remarks>
/// <para>
/// The Honua SDK is data-transport only: a real tool would fetch the input coverage to a
/// local file (or a <c>/vsi</c> handle) via <see cref="GpContext.Client"/> and
/// upload/register the output GeoTIFF. GDAL is the raster engine. Here the data path is
/// trivial/self-contained: if no input is staged we synthesize a 2-band (RED, NIR)
/// GeoTIFF with GDAL, then compute NDVI band math and write a single-band Float32
/// GeoTIFF.
/// </para>
/// <para>
/// Entrypoint spec for this tool: <c>"RasterTool::Honua.CustomCode.Samples.RasterTool"</c>.
/// Expected <c>params_json</c> (all optional):
/// <c>{"input": "scene.tif", "out_name": "ndvi.tif", "size": 64}</c>.
/// </para>
/// </remarks>
public sealed class RasterTool : IGeoprocessingTool
{
    private static int _initialized;

    /// <inheritdoc />
    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
    {
        // One-time GDAL init. GdalBase.ConfigureAll() wires the bundled native GDAL +
        // PROJ data and registers all drivers (it calls Gdal.AllRegister internally).
        // Idempotent-guard so repeated tool invocations in one process do not re-register.
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            GdalBase.ConfigureAll();
        }

        if (Gdal.GetDriverCount() <= 0)
        {
            return Task.FromResult(GpResult.Failed("GDAL initialized but registered no drivers."));
        }

        var outName = context.Params.TryGetProperty("out_name", out var n) &&
                      n.ValueKind == System.Text.Json.JsonValueKind.String
            ? n.GetString()!
            : "ndvi.tif";
        var size = context.Params.TryGetProperty("size", out var s) &&
                   s.ValueKind == System.Text.Json.JsonValueKind.Number
            ? Math.Clamp(s.GetInt32(), 2, 4096)
            : 64;

        context.Log.Info($"raster NDVI tool starting (GDAL {Gdal.VersionInfo("RELEASE_NAME")}, in-process)");
        context.Progress.Report(5.0, "resolving input");

        // Resolve the input: a staged input artifact, else synthesize a 2-band scene.
        string inputPath;
        if (context.Params.TryGetProperty("input", out var inEl) &&
            inEl.ValueKind == System.Text.Json.JsonValueKind.String &&
            inEl.GetString() is { Length: > 0 } inputKey &&
            context.Inputs.TryGetValue(inputKey, out var staged))
        {
            inputPath = staged;
            context.Log.Info($"using staged input '{inputKey}' -> {inputPath}");
        }
        else
        {
            inputPath = Path.Combine(context.WorkDirectory, "scene.tif");
            context.Log.Info("no input staged; synthesizing a 2-band scene with OSGeo.GDAL");
            SynthesizeScene(inputPath, size);
        }

        cancellationToken.ThrowIfCancellationRequested();
        context.Progress.Report(40.0, "reading bands");

        using var src = Gdal.Open(inputPath, Access.GA_ReadOnly)
            ?? throw new InvalidOperationException($"GDAL could not open {inputPath}.");
        if (src.RasterCount < 2)
        {
            return Task.FromResult(GpResult.Failed(
                $"input must have >= 2 bands (RED, NIR); got {src.RasterCount}."));
        }

        var width = src.RasterXSize;
        var height = src.RasterYSize;
        var count = width * height;
        var red = new float[count];
        var nir = new float[count];
        src.GetRasterBand(1).ReadRaster(0, 0, width, height, red, width, height, 0, 0);
        src.GetRasterBand(2).ReadRaster(0, 0, width, height, nir, width, height, 0, 0);

        context.Progress.Report(70.0, "computing NDVI");
        var ndvi = new float[count];
        for (var i = 0; i < count; i++)
        {
            var denom = nir[i] + red[i];
            ndvi[i] = denom == 0f ? 0f : (nir[i] - red[i]) / denom;
        }

        // Write a single-band Float32 GeoTIFF, preserving CRS + geotransform, LZW-compressed.
        var transform = new double[6];
        src.GetGeoTransform(transform);
        var projection = src.GetProjectionRef();

        var outPath = Path.Combine(context.WorkDirectory, outName);
        var driver = Gdal.GetDriverByName("GTiff");
        using (var dst = driver.Create(outPath, width, height, 1, DataType.GDT_Float32,
                   ["COMPRESS=LZW"]))
        {
            dst.SetGeoTransform(transform);
            if (!string.IsNullOrEmpty(projection))
            {
                dst.SetProjection(projection);
            }

            dst.GetRasterBand(1).WriteRaster(0, 0, width, height, ndvi, width, height, 0, 0);
            dst.FlushCache();
        }

        context.Progress.Report(90.0, "registering output");
        context.Output.AddArtifact(outName, outPath);

        float min = ndvi[0], max = ndvi[0];
        foreach (var v in ndvi)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        context.Log.Info(
            $"wrote {outName} ({new FileInfo(outPath).Length} bytes), NDVI range [{min:F3}, {max:F3}]");
        context.Progress.Report(100.0, "done");

        return Task.FromResult(GpResult.Succeeded($"NDVI GeoTIFF written to {outName}"));
    }

    /// <summary>
    /// Write a tiny 2-band (RED, NIR) Float32 GeoTIFF using <c>OSGeo.GDAL</c> + a real-ish
    /// UTM 33N CRS/geotransform, so the sample runs with no staged inputs.
    /// </summary>
    private static void SynthesizeScene(string path, int size)
    {
        var driver = Gdal.GetDriverByName("GTiff");
        using var ds = driver.Create(path, size, size, 2, DataType.GDT_Float32, null);
        ds.SetGeoTransform([500000.0, 10.0, 0.0, 4600000.0, 0.0, -10.0]);

        using var srs = new SpatialReference(string.Empty);
        srs.ImportFromEPSG(32633);
        srs.ExportToWkt(out var wkt, null);
        ds.SetProjection(wkt);

        var rng = new Random(42);
        var count = size * size;
        var red = new float[count];
        var nir = new float[count];
        for (var i = 0; i < count; i++)
        {
            red[i] = (float)(0.05 + (rng.NextDouble() * 0.35));
            nir[i] = (float)(0.3 + (rng.NextDouble() * 0.6));
        }

        ds.GetRasterBand(1).WriteRaster(0, 0, size, size, red, size, size, 0, 0);
        ds.GetRasterBand(2).WriteRaster(0, 0, size, size, nir, size, size, 0, 0);
        ds.FlushCache();
    }
}
