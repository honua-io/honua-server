// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public readonly record struct SourceSpan(int Line, int Column, int Offset, int Length)
{
    public static SourceSpan Synthetic { get; } = new(0, 0, 0, 0);

    public bool HasLocation => Line > 0 && Column > 0;
}
