// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Operators;

/// <summary>
/// In-code registry of the S1 operator set. This is intentionally keyed on
/// a constant version string so that downstream consumers can compare against
/// <see cref="SpecGrammarVersion.CurrentOperatorCapability"/>.
/// </summary>
public interface IOperatorCatalog
{
    /// <summary>
    /// Capability version advertised by this catalog. Grows independently of
    /// the grammar version so that non-breaking operator additions do not
    /// force a grammar bump.
    /// </summary>
    string CapabilityVersion { get; }

    /// <summary>
    /// Looks up an operator signature by name. Returns <c>null</c> when the
    /// operator is not registered (the caller emits an
    /// <see cref="SpecDiagnosticCode.UnknownOperator"/> diagnostic).
    /// </summary>
    /// <param name="name">Operator name.</param>
    /// <returns>Signature or <c>null</c>.</returns>
    OperatorSignature? Lookup(string name);

    /// <summary>
    /// Enumerates all registered operator names.
    /// </summary>
    IReadOnlyCollection<string> OperatorNames { get; }
}

/// <summary>
/// Default in-code operator catalog covering the six S1 operators defined by
/// the design: <c>filter</c>, <c>spatial_join</c>, <c>buffer</c>,
/// <c>reproject</c>, <c>zonal_stats</c>, <c>slope</c>.
/// </summary>
public sealed class OperatorCatalog : IOperatorCatalog
{
    private readonly FrozenDictionary<string, OperatorSignature> _signatures;

    /// <summary>
    /// Creates the default S1 catalog.
    /// </summary>
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

    /// <inheritdoc />
    public string CapabilityVersion => SpecGrammarVersion.CurrentOperatorCapability;

    /// <inheritdoc />
    public IReadOnlyCollection<string> OperatorNames => _signatures.Keys;

    /// <inheritdoc />
    public OperatorSignature? Lookup(string name)
        => _signatures.TryGetValue(name, out var signature) ? signature : null;
}
