// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Studio.Export;

internal sealed class StudioDeliverableRenderException : Exception
{
    public StudioDeliverableRenderException(string code, string detail)
        : base(detail)
    {
        Code = code;
    }

    public string Code { get; }
}
