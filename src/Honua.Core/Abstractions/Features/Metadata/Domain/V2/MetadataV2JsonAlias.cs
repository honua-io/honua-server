// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

// rc.2 writers retain unknown canonical aliases while editing legacy properties.
// Legacy presence therefore wins even for empty/null values and in either JSON order.
// Alias properties use ordinary setters so source generation assigns them after
// OnDeserializing, including on .NET 10; init accessors run during construction.
// Keep this state as a value so a record's with-expression cannot mutate its source.
internal readonly record struct MetadataV2JsonAlias<T>(T? Value)
{
    private bool IsReading { get; init; }
    private bool HasLegacyValue { get; init; }

    public MetadataV2JsonAlias<T> BeginRead() => this with { IsReading = true, HasLegacyValue = false };

    public MetadataV2JsonAlias<T> CompleteRead() => this with { IsReading = false, HasLegacyValue = false };

    public MetadataV2JsonAlias<T> WithLegacy(T? value) => this with { Value = value, HasLegacyValue = IsReading };

    public MetadataV2JsonAlias<T> WithCanonical(T? value) =>
        IsReading && HasLegacyValue ? this : this with { Value = value, HasLegacyValue = false };
}
