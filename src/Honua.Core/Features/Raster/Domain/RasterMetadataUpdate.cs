// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public sealed record RasterMetadataUpdate
{
    public string? Name { get; init; }

    public Optional<string?> Description { get; init; }

    public Optional<DateTimeOffset?> AcquisitionDate { get; init; }

    public bool HasAnyChange => Name is not null || Description.IsSet || AcquisitionDate.IsSet;
}

public readonly record struct Optional<T>
{
    internal Optional(T value, bool isSet)
    {
        Value = value;
        IsSet = isSet;
    }

    public T Value { get; }

    public bool IsSet { get; }
}

public static class Optional
{
    public static Optional<T> Of<T>(T value) => new(value, true);

    public static Optional<T> Unset<T>() => default;
}
