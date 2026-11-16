// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Operators;

public interface IOperatorCatalog
{
    string CapabilityVersion { get; }

    OperatorSignature? Lookup(string name);

    IReadOnlyCollection<string> OperatorNames { get; }
}

public sealed class OperatorCatalog : IOperatorCatalog
{
    private readonly FrozenDictionary<string, OperatorSignature> _signatures;

    public OperatorCatalog()
    {
        var datasetInput = new OperatorPort("input", TypeRef.Intrinsic(SpecTypeKind.Dataset));
        var rasterInput = new OperatorPort("input", TypeRef.Intrinsic(SpecTypeKind.Raster));

        var defs = new List<OperatorSignature>
        {
            // filter(input: dataset) -> dataset
            new(
                Name: "filter",
                Inputs: ImmutableArray.Create(datasetInput),
                Parameters: ImmutableArray.Create(
                    new OperatorPort("where", TypeRef.Intrinsic(SpecTypeKind.String), Required: true)),
                Output: TypeRef.Intrinsic(SpecTypeKind.Dataset)),

            // spatial_join(left, right) -> dataset; optional distance (projected CRS)
            new(
                Name: "spatial_join",
                Inputs: ImmutableArray.Create(
                    new OperatorPort("left", TypeRef.Intrinsic(SpecTypeKind.Dataset)),
                    new OperatorPort("right", TypeRef.Intrinsic(SpecTypeKind.Dataset))),
                Parameters: ImmutableArray.Create(
                    new OperatorPort("distance", TypeRef.Intrinsic(SpecTypeKind.Distance), Required: false),
                    new OperatorPort("crs", TypeRef.Intrinsic(SpecTypeKind.Crs), Required: false)),
                Output: TypeRef.Intrinsic(SpecTypeKind.Dataset),
                CrsSensitive: true),

            // buffer(input) -> dataset; required distance
            new(
                Name: "buffer",
                Inputs: ImmutableArray.Create(datasetInput),
                Parameters: ImmutableArray.Create(
                    new OperatorPort("distance", TypeRef.Intrinsic(SpecTypeKind.Distance), Required: true),
                    new OperatorPort("crs", TypeRef.Intrinsic(SpecTypeKind.Crs), Required: false)),
                Output: TypeRef.Intrinsic(SpecTypeKind.Dataset),
                CrsSensitive: true),

            // reproject(input) -> dataset|raster (same kind as input); required crs
            new(
                Name: "reproject",
                Inputs: ImmutableArray.Create(datasetInput),
                Parameters: ImmutableArray.Create(
                    new OperatorPort("crs", TypeRef.Intrinsic(SpecTypeKind.Crs), Required: true)),
                Output: TypeRef.Intrinsic(SpecTypeKind.Unknown)),

            // zonal_stats(zones: dataset, values: raster) -> dataset
            new(
                Name: "zonal_stats",
                Inputs: ImmutableArray.Create(
                    new OperatorPort("zones", TypeRef.Intrinsic(SpecTypeKind.Dataset)),
                    new OperatorPort("values", TypeRef.Intrinsic(SpecTypeKind.Raster))),
                Parameters: ImmutableArray.Create(
                    new OperatorPort("statistics", TypeRef.Intrinsic(SpecTypeKind.String), Required: false)),
                Output: TypeRef.Intrinsic(SpecTypeKind.Dataset)),

            // slope(input: raster) -> raster
            new(
                Name: "slope",
                Inputs: ImmutableArray.Create(rasterInput),
                Parameters: ImmutableArray.Create(
                    new OperatorPort("unit", TypeRef.Intrinsic(SpecTypeKind.String), Required: false)),
                Output: TypeRef.Intrinsic(SpecTypeKind.Raster))
        };

        _signatures = defs.ToFrozenDictionary(sig => sig.Name, StringComparer.Ordinal);
    }

    public string CapabilityVersion => SpecGrammarVersion.CurrentOperatorCapability;

    public IReadOnlyCollection<string> OperatorNames => _signatures.Keys;

    public OperatorSignature? Lookup(string name)
        => _signatures.TryGetValue(name, out var signature) ? signature : null;
}
