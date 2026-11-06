// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

public sealed record CsvGeocodedAddress(double Longitude, double Latitude);

public delegate Task<CsvGeocodedAddress?> CsvAddressGeocoder(string address, CancellationToken cancellationToken);

public sealed record CsvImportOptions
{
    public string? LongitudeColumn { get; init; }

    public string? LatitudeColumn { get; init; }

    public string? AddressColumn { get; init; }

    public CsvAddressGeocoder? AddressGeocoder { get; init; }

    public int? MaxGeocodedRows { get; init; }
}

// The message is built only from caller-supplied options and CSV header names, so it is safe to show to a client.
public sealed class CsvImportOptionsException : Exception
{
    public CsvImportOptionsException(string message)
        : base(message)
    {
    }
}
