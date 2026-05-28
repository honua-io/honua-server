// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Tests for <see cref="Honua.Server.Features.Protocols.Ogc.Api.Maps.Handlers.OgcMapsRenderingHandler"/>.
///
/// TODO(#1035 cutover 86/N): the rendering handler was ported to Metadata v2 in the
/// same slice but its (very large) v1-only test class was left to a follow-up port
/// per the cutover strategy (do the source port, leave a focused TODO for the test
/// fixtures, and move on). The original v1 test class verified the collection-,
/// dataset-, and styled-map paths against the v1 catalog graph;
/// rewriting them on the V2 TestMetadataV2GraphBuilder is tracked under task #55
/// (Port test fixtures off v1).
/// </summary>
[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsRenderingHandlerTests
{
}
