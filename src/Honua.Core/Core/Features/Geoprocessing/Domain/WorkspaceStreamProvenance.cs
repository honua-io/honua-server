// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>Reserved server-written provenance for managed stream workspace publications.</summary>
public static class WorkspaceStreamProvenance
{
    /// <summary>Reserved metadata prefix that ordinary artifact registration cannot supply.</summary>
    public const string Prefix = "honua.stream.";
    /// <summary>Server publication marker.</summary>
    public const string Published = Prefix + "published";
    /// <summary>Immutable object key metadata.</summary>
    public const string ObjectKey = Prefix + "objectKey";
    /// <summary>Configured shared-store identity metadata.</summary>
    public const string StoreReference = Prefix + "storeReference";
}
