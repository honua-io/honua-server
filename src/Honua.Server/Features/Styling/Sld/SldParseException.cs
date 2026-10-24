// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Styling.Sld;

/// <summary>
/// Thrown when an SLD document cannot be parsed: malformed XML, unsafe XML,
/// or absence of a recognizable Styled Layer Descriptor root element.
/// </summary>
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
