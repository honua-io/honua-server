// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

// The row is valid only inside the mapper callback and may be reused, so the mapper must not capture it.
public interface IDatabaseRow
{
    int FieldCount { get; }

    bool IsNull(int ordinal);

    T GetFieldValue<T>(int ordinal);

    int GetOrdinal(string name);
}
