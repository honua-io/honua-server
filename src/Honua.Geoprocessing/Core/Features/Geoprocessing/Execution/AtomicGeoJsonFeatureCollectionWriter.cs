// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Honua.Geoprocessing.Execution;

internal sealed record GeoJsonFeatureCollectionWriteRequest
{
    public required string ResolvedPath { get; init; }

    public required IAsyncEnumerable<IFeature> Source { get; init; }

    public required IJobExecutionContext Context { get; init; }

    public required Action<GeoJsonWriter, JsonTextWriter, IFeature> WriteFeature { get; init; }
}

internal static class AtomicGeoJsonFeatureCollectionWriter
{
    public static async Task WriteAndCommitAsync(
        GeoJsonFeatureCollectionWriteRequest request,
        CancellationToken cancellationToken)
    {
        // Write to a sibling temp file first and atomically move it onto the destination
        // only after a fully successful write so failures never leave partial outputs.
        var tempPath = request.ResolvedPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var committed = false;
        try
        {
            await WriteTempFileAsync(request, tempPath, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await request.Context.ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, request.ResolvedPath, overwrite: false);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                FileSinkInputReader.TryDeleteTempFile(tempPath);
            }
        }
    }

    private static async Task WriteTempFileAsync(
        GeoJsonFeatureCollectionWriteRequest request,
        string tempPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.ResolvedPath)!);
        var geoJsonWriter = new GeoJsonWriter();
        await using var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
        await using var textWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var jsonWriter = new JsonTextWriter(textWriter);

        await jsonWriter.WriteStartObjectAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.WritePropertyNameAsync("type", cancellationToken).ConfigureAwait(false);
        await jsonWriter.WriteValueAsync("FeatureCollection", cancellationToken).ConfigureAwait(false);
        await jsonWriter.WritePropertyNameAsync("features", cancellationToken).ConfigureAwait(false);
        await jsonWriter.WriteStartArrayAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var feature in request.Source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.WriteFeature(geoJsonWriter, jsonWriter, feature);
        }

        await jsonWriter.WriteEndArrayAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.WriteEndObjectAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
