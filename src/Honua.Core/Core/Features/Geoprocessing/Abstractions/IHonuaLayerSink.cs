// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public enum HonuaLayerLoadMode
{
    Append,

    Replace,

    Upsert,
}

public sealed record HonuaLayerSinkRow(byte[] WellKnownBinary, string AttributesJson);

public sealed record HonuaLayerSinkRequest(
    string Schema,
    string Table,
    string GeometryColumn,
    int TargetSrid,
    HonuaLayerLoadMode LoadMode,
    string BatchId,
    IReadOnlyList<string> KeyFields);

public sealed record HonuaLayerSinkOutcome(
    long FeaturesWritten,
    string Schema,
    string Table,
    string BatchId);

public interface IHonuaLayerSink
{
    Task<HonuaLayerSinkOutcome> LoadAsync(
        HonuaLayerSinkRequest request,
        IReadOnlyList<HonuaLayerSinkRow> rows,
        CancellationToken cancellationToken);
}
