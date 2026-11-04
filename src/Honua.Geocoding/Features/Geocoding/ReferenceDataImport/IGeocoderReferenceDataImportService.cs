// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.ReferenceDataImport;

public interface IGeocoderReferenceDataImportService
{
    Task<GeocoderReferenceDataImportResult> ImportAsync(
        GeocoderReferenceDataImportRequest request,
        CancellationToken cancellationToken = default);
}
