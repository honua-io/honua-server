// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Test helper that materializes the WFS 2.0 <see cref="FilterCapabilities"/> produced by
/// the runtime. The production builder is a private static member of <see cref="Wfs20Handler"/>,
/// so it is invoked via reflection here to keep the assertion suites pinned to the exact
/// capabilities the server advertises.
/// </summary>
internal static class Wfs20FilterCapabilitiesFactory
{
    /// <summary>
    /// Invokes the runtime's private <c>BuildFilterCapabilities</c> factory and returns its result.
    /// </summary>
    public static FilterCapabilities Build()
    {
        var method = typeof(Wfs20Handler).GetMethod(
            "BuildFilterCapabilities",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("Wfs20Handler.BuildFilterCapabilities should exist");

        return (FilterCapabilities)method!.Invoke(null, null)!;
    }
}
