// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

// Open while two releases can answer the same address with different bytes.
internal interface IRolloutWindow
{
    bool IsOpen { get; }
}

internal sealed class ClosedRolloutWindow : IRolloutWindow
{
    public bool IsOpen => false;
}
