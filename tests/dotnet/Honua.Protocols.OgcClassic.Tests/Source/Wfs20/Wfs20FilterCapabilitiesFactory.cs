// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Test helper that materializes the WFS 2.0 <see cref="FilterCapabilities"/> produced by
/// the runtime.
/// </summary>
internal static class Wfs20FilterCapabilitiesFactory
{
    /// <summary>
    /// Invokes the runtime's filter capabilities factory and returns its result.
    /// </summary>
    public static FilterCapabilities Build()
    {
        return Wfs20Handler.BuildFilterCapabilities();
    }
}
