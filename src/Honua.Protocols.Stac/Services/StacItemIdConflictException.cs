// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Stac.Services;

/// <summary>
/// Raised when a requested STAC item id matches more than one feature.
/// Callers map this to HTTP 409.
/// </summary>
internal sealed class StacItemIdConflictException : Exception
{
    public StacItemIdConflictException(string message)
        : base(message)
    {
    }
}
