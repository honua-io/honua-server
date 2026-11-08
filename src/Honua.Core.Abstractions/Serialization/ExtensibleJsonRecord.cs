// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Serialization;

public abstract record ExtensibleJsonRecord
{
    // Source generation binds init-only members as constructor arguments, which extension data cannot use.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public virtual bool Equals(ExtensibleJsonRecord? other)
        => other is not null && EqualityContract == other.EqualityContract &&
           JsonExtensionDataEquality.Equals(AdditionalProperties, other.AdditionalProperties);

    public override int GetHashCode()
        => HashCode.Combine(EqualityContract, JsonExtensionDataEquality.PropertyNamesHashCode(AdditionalProperties));
}
