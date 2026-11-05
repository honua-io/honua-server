// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Styling.Sld;

internal sealed class SldParseException : Exception
{
    public SldParseException(string message)
        : base(message)
    {
    }

    public SldParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
