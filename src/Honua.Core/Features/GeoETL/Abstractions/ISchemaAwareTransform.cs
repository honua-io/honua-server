// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;

namespace Honua.Core.Features.GeoETL.Abstractions;

/// <summary>
/// The effect a transform has on the attribute schema flowing through a pipeline. The
/// stage-chain validator uses this to fail a pipeline fast — at CRUD / pre-execution
/// time rather than mid-run — when a stage requires an attribute field that no upstream
/// stage produces. See the GeoETL roadmap § Transform library and ADR-0038
/// § Stage-chain validation.
/// </summary>
/// <param name="RequiredFields">
/// Attribute field names this transform reads. The validator reports a hard failure when
/// a required field is neither declared by the source nor produced by an earlier stage.
/// </param>
/// <param name="ProducedFields">
/// Attribute field names this transform adds to the output schema.
/// </param>
/// <param name="RemovedFields">
/// Attribute field names this transform removes from the output schema (for example a
/// rename removes the source key). Empty when the transform removes nothing.
/// </param>
public sealed record TransformSchemaEffect(
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string> ProducedFields,
    IReadOnlyList<string> RemovedFields)
{
    /// <summary>
    /// A schema effect that neither requires, produces, nor removes any field.
    /// </summary>
    public static readonly TransformSchemaEffect None = new([], [], []);
}

/// <summary>
/// Implemented by transforms that can describe their attribute-schema effect so the
/// stage-chain validator can check field reachability before a pipeline runs. Transforms
/// that do not implement this interface are treated as schema-passthrough (no required,
/// produced, or removed fields) and never block validation.
/// </summary>
public interface ISchemaAwareTransform
{
    /// <summary>
    /// Describes how this transform reads and reshapes the attribute schema for the given
    /// configuration.
    /// </summary>
    /// <param name="config">The transform configuration.</param>
    /// <returns>The transform's schema effect.</returns>
    TransformSchemaEffect DescribeSchema(TransformConfig config);
}
