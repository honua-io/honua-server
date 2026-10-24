// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests;

/// <summary>
/// Collection definition for tests that run reference external server containers.
/// </summary>
[CollectionDefinition("ExternalServer", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public sealed class ExternalServerCollection
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition].
}
