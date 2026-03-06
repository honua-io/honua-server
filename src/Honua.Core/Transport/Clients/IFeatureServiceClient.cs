// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Models;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Core.Transport.Clients;

/// <summary>
/// Generic interface for feature service clients that work across different platforms.
/// TContext represents platform-specific context (e.g., HttpClient for server, custom auth for mobile).
/// </summary>
/// <typeparam name="TContext">Platform-specific context type</typeparam>
public interface IFeatureServiceClient<TContext>
{
    /// <summary>
    /// Executes a feature query and returns all results in a single response.
    /// Use for small result sets or when you need all data at once.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Platform-specific context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results with features</returns>
    Task<Features.FeatureStore.Domain.QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a feature query and streams results as pages.
    /// Use for large result sets to avoid memory issues and improve responsiveness.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Platform-specific context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of feature pages</returns>
    IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies feature edits (add/update/delete operations) to a service layer.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="edits">Edit operations to apply</param>
    /// <param name="context">Platform-specific context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Edit results with success/failure status for each operation</returns>
    Task<EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        TContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a page of features from a streaming query.
/// </summary>
public class FeaturePage
{
    /// <summary>
    /// Features in this page.
    /// </summary>
    public ImmutableArray<DomainFeature> Features { get; set; } = ImmutableArray<DomainFeature>.Empty;

    /// <summary>
    /// Metadata populated on the first page only.
    /// </summary>
    public PageMetadata? Metadata { get; set; }

    /// <summary>
    /// True when this is the final page in the stream.
    /// </summary>
    public bool IsLastPage { get; set; }
}

/// <summary>
/// Metadata for a feature query result, provided on the first page of streaming results.
/// </summary>
public class PageMetadata
{
    /// <summary>
    /// Name of the field that contains object IDs.
    /// </summary>
    public string? ObjectIdFieldName { get; set; }

    /// <summary>
    /// Geometry type of features in the layer.
    /// </summary>
    public string? GeometryType { get; set; }

    /// <summary>
    /// Spatial reference system of the layer.
    /// </summary>
    public SpatialReference? SpatialReference { get; set; }

    /// <summary>
    /// Field definitions for the layer.
    /// </summary>
    public ImmutableArray<FieldDefinition> Fields { get; set; } = ImmutableArray<FieldDefinition>.Empty;
}

/// <summary>
/// Represents feature editing operations.
/// </summary>
public class FeatureEdits
{
    /// <summary>
    /// Features to create (insert).
    /// </summary>
    public ImmutableArray<DomainFeature> Adds { get; set; } = ImmutableArray<DomainFeature>.Empty;

    /// <summary>
    /// Features to update (modify existing).
    /// </summary>
    public ImmutableArray<DomainFeature> Updates { get; set; } = ImmutableArray<DomainFeature>.Empty;

    /// <summary>
    /// Feature IDs to delete.
    /// </summary>
    public ImmutableArray<long> Deletes { get; set; } = ImmutableArray<long>.Empty;

    /// <summary>
    /// Whether to rollback all changes if any operation fails.
    /// </summary>
    public bool RollbackOnFailure { get; set; } = true;

    /// <summary>
    /// Whether to force write operations, bypassing optimistic locking.
    /// </summary>
    public bool ForceWrite { get; set; }
}

/// <summary>
/// Results from applying feature edits.
/// </summary>
public class EditResult
{
    /// <summary>
    /// Results for add operations.
    /// </summary>
    public ImmutableArray<OperationResult> AddResults { get; set; } = ImmutableArray<OperationResult>.Empty;

    /// <summary>
    /// Results for update operations.
    /// </summary>
    public ImmutableArray<OperationResult> UpdateResults { get; set; } = ImmutableArray<OperationResult>.Empty;

    /// <summary>
    /// Results for delete operations.
    /// </summary>
    public ImmutableArray<OperationResult> DeleteResults { get; set; } = ImmutableArray<OperationResult>.Empty;

    /// <summary>
    /// Global error if the entire operation failed.
    /// </summary>
    public EditError? Error { get; set; }
}

/// <summary>
/// Result of a single edit operation.
/// </summary>
public class OperationResult
{
    /// <summary>
    /// Object ID of the feature that was affected.
    /// </summary>
    public long ObjectId { get; set; }

    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error details if the operation failed.
    /// </summary>
    public EditError? Error { get; set; }
}

/// <summary>
/// Error details for failed edit operations.
/// </summary>
public class EditError
{
    /// <summary>
    /// Error code.
    /// </summary>
    public int Code { get; set; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Field definition describing layer schema.
/// </summary>
public class FieldDefinition
{
    /// <summary>
    /// Field name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Field data type.
    /// </summary>
    public string FieldType { get; set; } = string.Empty;

    /// <summary>
    /// Maximum length for string fields.
    /// </summary>
    public int? Length { get; set; }

    /// <summary>
    /// Whether the field allows null values.
    /// </summary>
    public bool Nullable { get; set; } = true;

    /// <summary>
    /// Display name for the field.
    /// </summary>
    public string? Alias { get; set; }
}