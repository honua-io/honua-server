// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.Tests;

// Hosts in one database share the job tables, so the classes that run jobs through them take turns.
[CollectionDefinition(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class JobSubstrateCollection
{
}
