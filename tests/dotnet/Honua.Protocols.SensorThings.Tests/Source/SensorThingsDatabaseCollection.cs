// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

// Collection definitions must be in the test assembly. Reuse the shared fixture
// contract while preserving the existing per-test WebAppFixture schema isolation.
// The collection-scoped WebAppFixture holds a reference on the shared default host,
// so default-configuration cases reuse it instead of rebuilding it per case; each
// case still creates and seeds its own schema.
[CollectionDefinition("Database", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definitions use the Collection suffix.")]
public sealed class SensorThingsDatabaseCollection : DatabaseCollection, ICollectionFixture<WebAppFixture>;
