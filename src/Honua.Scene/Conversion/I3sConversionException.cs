// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Conversion;

public sealed class I3sConversionException : Exception
{
    public I3sConversionException(I3sConversionErrorReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public I3sConversionException(I3sConversionErrorReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public I3sConversionErrorReason Reason { get; }
}

public enum I3sConversionErrorReason
{
    MalformedSceneLayer,

    MissingExtent,

    UnsupportedLayerType,

    UnsupportedSpatialReference,
}
