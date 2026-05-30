// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Hosting;

/// <summary>
/// Internal marker type for the Honua.Hosting assembly. Honua.Hosting carves
/// the protocol-required sub-areas of <c>Honua.Infrastructure</c>
/// (Authentication, Models, Events, Validation, Helpers, Caching) into a
/// dedicated assembly so the Honua.Protocols.* assemblies can ProjectReference
/// the hosting surface without forming a cycle with Honua.Server. See
/// docs/contributor/adr/0044-server-infrastructure-decomposition.md.
/// </summary>
internal static class AssemblyMarker
{
}
