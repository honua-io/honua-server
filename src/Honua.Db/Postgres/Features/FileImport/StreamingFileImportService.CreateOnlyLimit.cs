// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private async IAsyncEnumerable<IFeature> ReadGeoJsonFeatures(
        Stream source, ImportLoadMode loadMode, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = loadMode == ImportLoadMode.CreateOnly
            ? new StreamingGeoJsonReader(_limits with { MaxFeaturesPerFile = 0 })
            : _geoJsonReader;
        int? expectedCount = loadMode == ImportLoadMode.CreateOnly
            ? await ValidateCreateOnlyGeoJsonAsync(reader, source, cancellationToken)
            : null;
        var count = 0;
        await foreach (var feature in reader.ReadFeaturesAsync(source, cancellationToken))
        {
            count++;
            yield return feature;
        }
        if (expectedCount.HasValue && count != expectedCount.Value)
        {
            throw new InvalidDataException("Create-only GeoJSON input was not read completely.");
        }
    }

    private static async Task<int> ValidateCreateOnlyGeoJsonAsync(
        StreamingGeoJsonReader reader, Stream source, CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
        {
            throw new InvalidDataException("Create-only GeoJSON input must support complete validation and replay.");
        }
        var validation = await reader.ValidateAsync(source, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Create-only GeoJSON input failed complete validation.");
        }
        return validation.FeatureCount;
    }

    private async IAsyncEnumerable<IFeature> EnforceCreateOnlyFeatureLimit(
        IAsyncEnumerable<IFeature> features, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0L;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            if (_limits.MaxFeaturesPerFile > 0 && ++count > _limits.MaxFeaturesPerFile)
            {
                throw new InvalidDataException("Create-only input exceeds the maximum feature count.");
            }
            yield return feature;
        }
    }
}
