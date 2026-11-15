// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

// rc.2 writers retain unknown canonical aliases while editing legacy properties.
// Legacy presence therefore wins even for empty/null values and in either JSON order.
// Alias properties use ordinary setters so source generation assigns them after
// OnDeserializing, including on .NET 10; init accessors run during construction.
// Keep this state as a value so a record's with-expression cannot mutate its source.
internal readonly record struct MetadataV2JsonAlias<T>(T? Value, bool HasLegacyValue)
{
    public MetadataV2JsonAlias<T> CompleteRead() => new(Value, false);

    public MetadataV2JsonAlias<T> WithLegacy(T? value, bool readingJson) => this with { Value = value, HasLegacyValue = readingJson };

    public MetadataV2JsonAlias<T> WithCanonical(T? value, bool readingJson) => readingJson && HasLegacyValue ? this : new(value, false);
}
