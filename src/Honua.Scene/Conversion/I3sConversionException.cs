// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// Raised when an I3S scene layer or <c>.slpk</c> package cannot be parsed or
/// converted to a Honua 3D Tiles tileset. The <see cref="Reason"/> is a stable,
/// non-sensitive discriminator suitable for telemetry and structured error
/// responses; the message never echoes raw archive contents or filesystem
/// paths.
/// </summary>
public sealed class I3sConversionException : Exception
{
    /// <summary>Initializes the exception with a stable reason code and message.</summary>
    /// <param name="reason">Stable reason discriminator (see <see cref="I3sConversionErrorReason"/>).</param>
    /// <param name="message">Human-readable, non-sensitive message.</param>
    public I3sConversionException(I3sConversionErrorReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Initializes the exception with a reason, message, and inner cause.</summary>
    /// <param name="reason">Stable reason discriminator.</param>
    /// <param name="message">Human-readable, non-sensitive message.</param>
    /// <param name="innerException">Underlying cause.</param>
    public I3sConversionException(I3sConversionErrorReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Stable reason discriminator for telemetry and error mapping.</summary>
    public I3sConversionErrorReason Reason { get; }
}

/// <summary>
/// Stable reason codes for <see cref="I3sConversionException"/>. Values are
/// safe to surface in telemetry and structured error responses.
/// </summary>
public enum I3sConversionErrorReason
{
    /// <summary>The <c>.slpk</c> archive could not be opened as a zip container.</summary>
    InvalidArchive,

    /// <summary>The archive did not contain a <c>3dSceneLayer.json</c> descriptor.</summary>
    MissingSceneLayer,

    /// <summary>The <c>3dSceneLayer.json</c> document was not valid JSON.</summary>
    MalformedSceneLayer,

    /// <summary>The scene layer is missing the geographic extent needed to place the tileset.</summary>
    MissingExtent,

    /// <summary>The layer type is not supported by the current converter slice.</summary>
    UnsupportedLayerType,

    /// <summary>The layer uses a spatial reference the converter cannot place geographically.</summary>
    UnsupportedSpatialReference,
}
