// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

public sealed record UnsupportedSymbolizerInfo
{
    public required string Code { get; init; }

    public required string SymbolizerType { get; init; }

    public required string Guidance { get; init; }
}
