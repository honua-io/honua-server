// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.ObjectStorage;

// Folders a component writes in the installation bucket from its own configuration. Built-in connections
// never cover them.
public interface IObjectStorageServerFolders
{
    IEnumerable<string> Folders { get; }
}

public sealed class GeoprocessingOutputServerFolders(IOptionsMonitor<GeoprocessingOutputStagingOptions> options) : IObjectStorageServerFolders
{
    public IEnumerable<string> Folders => [options.CurrentValue.KeyPrefix];
}

// Everything the built-in connections are derived from, read fresh on each call.
public sealed class ObjectStorageBuiltInSources(
    IOptionsMonitor<CloudStorageOptions> storage,
    IOptionsMonitor<ObjectStorageConnectionOptions> connections,
    IEnumerable<IObjectStorageServerFolders> serverFolders)
{
    public IReadOnlyList<ObjectStorageConnection> BuiltIns()
        => ObjectStorageBuiltInConnections.From(
            storage.CurrentValue,
            connections.CurrentValue.BuiltIn,
            serverFolders.SelectMany(source => source.Folders));
}
