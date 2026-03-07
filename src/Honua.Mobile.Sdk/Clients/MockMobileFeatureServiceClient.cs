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
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Honua.Core.Models;
using Honua.Core.Transport.Clients;
using Honua.Core.Transport.Converters;
using Honua.Core.Features.FeatureStore.Domain;
using NetTopologySuite.Geometries;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Clients;

/// <summary>
/// Mock implementation of mobile feature service client for testing.
/// This will be replaced with the real implementation when Honua.Core.Sdk is available.
/// </summary>
public class MockMobileFeatureServiceClient : IFeatureServiceClient<MobileContext>
{
    private readonly ILogger<MockMobileFeatureServiceClient> _logger;

    public MockMobileFeatureServiceClient(ILogger<MockMobileFeatureServiceClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Mock query for service {ServiceId}, layer {LayerId}", serviceId, layerId);

        await Task.Delay(100, cancellationToken); // Simulate network delay

        // Create mock features
        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749));
        var pointWkb = GeometryConverter.ToWkb(point);

        var features = new[]
        {
            new DomainFeature
            {
                Id = 1,
                Attributes = new Dictionary<string, object?>
                {
                    ["Name"] = "Mock Feature 1",
                    ["Type"] = "Test"
                }.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Geometry = pointWkb
            },
            new DomainFeature
            {
                Id = 2,
                Attributes = new Dictionary<string, object?>
                {
                    ["Name"] = "Mock Feature 2",
                    ["Type"] = "Test"
                }.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Geometry = pointWkb
            }
        };

        var recordCount = query.ResultRecordCount ?? features.Length;
        var offset = query.ResultOffset ?? 0;

        var resultFeatures = features
            .Skip(offset)
            .Take(recordCount)
            .ToImmutableArray();

        return QueryResult<DomainFeature>.Create(features.Length, resultFeatures);
    }

    public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        MobileContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Mock streaming query for service {ServiceId}, layer {LayerId}", serviceId, layerId);

        // Simulate streaming pages
        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749));
        var pointWkb = GeometryConverter.ToWkb(point);

        var pageSize = query.ResultRecordCount ?? 10;
        var totalFeatures = 25; // Mock total

        for (int page = 0; page * pageSize < totalFeatures; page++)
        {
            await Task.Delay(50, cancellationToken); // Simulate network delay

            var start = page * pageSize;
            var count = Math.Min(pageSize, totalFeatures - start);

            var features = Enumerable.Range(start, count)
                .Select(i => new DomainFeature
                {
                    Id = i + 1,
                    Attributes = new Dictionary<string, object?>
                    {
                        ["Name"] = $"Mock Stream Feature {i + 1}",
                        ["Type"] = "Stream"
                    }.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    Geometry = pointWkb
                })
                .ToImmutableArray();

            yield return new FeaturePage
            {
                Features = features,
                IsLastPage = (start + count) >= totalFeatures,
                PageNumber = page
            };
        }
    }

    public async Task<EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Mock apply edits for service {ServiceId}, layer {LayerId}", serviceId, layerId);

        await Task.Delay(200, cancellationToken); // Simulate network delay

        var addResults = edits.Adds.Select((_, i) => new OperationResult
        {
            ObjectId = 1000 + i,
            Success = true
        }).ToImmutableArray();

        var updateResults = edits.Updates.Select(f => new OperationResult
        {
            ObjectId = f.Id,
            Success = true
        }).ToImmutableArray();

        var deleteResults = edits.Deletes.Select(id => new OperationResult
        {
            ObjectId = id,
            Success = true
        }).ToImmutableArray();

        return new EditResult
        {
            AddResults = addResults,
            UpdateResults = updateResults,
            DeleteResults = deleteResults
        };
    }

    public Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = new MobileContext { CancellationToken = cancellationToken };
        return QueryFeaturesAsync(serviceId, layerId, query, context, cancellationToken);
    }
}
