// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Redshift.Features.FeatureStore;
using Honua.Db.Redshift.Features.FeatureStore.Services;
using Honua.Tests.ProviderUnitSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Db.Redshift.Tests;

/// <summary>
/// Verifies read-policy parity for the Redshift provider: a bound reader refuses a read when a
/// permanent filter, row-level security predicate or field mask applies to the layer (the
/// provider applies none of them), and reads exactly as before when nothing resolves.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public class RedshiftFeatureStoreReadPolicyTests
{
    private const int LayerId = 1;

    private static readonly ParcelsLayerFixture Parcels = new(DataProviderNames.Redshift, "public.parcels")
    {
        GeometryFieldType = MetadataV2FieldType.Geography
    };

    public static TheoryData<string> ReadOperations => new() { "get", "query", "ids", "count", "extent", "estimates", "statistics" };

    [Fact]
    public void Capabilities_SupportStatisticsOnADedicatedDeclaration()
    {
        var store = CreateStore(new RecordingConnectionFactory(), readSecurity: null);

        Assert.True(store.Capabilities.SupportsStatistics);
        Assert.NotSame(FeatureProviderCapabilities.ReadOnlyMySql, store.Capabilities);
        Assert.True(store.Capabilities.SupportsQuery);
        Assert.True(store.Capabilities.SupportsCount);
        Assert.True(store.Capabilities.SupportsExtent);
        Assert.False(store.Capabilities.SupportsReadPolicyEnforcement);
        Assert.Equal(FeatureProviderEditCapabilities.ReadOnly, store.Capabilities.Edits);
        Assert.False(store.Capabilities.Outputs.SupportsStreamingGeoJson);
        Assert.False(store.Capabilities.Outputs.SupportsNativeMvt);
        Assert.False(store.Capabilities.Outputs.SupportsNativeFlatGeobuf);
        Assert.False(store.Capabilities.Outputs.SupportsNativeGeobuf);
        Assert.False(store.Capabilities.Outputs.SupportsNativeGml);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithRowLevelSecurityPredicateResolved_RefusesReadBeforeConnecting(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var readSecurity = new LayerReadSecurityResolver(
            v2Provider: null,
            filterExpressionService: null,
            new StubRowFilterSource(new Honua.Core.Queries.Filters.SqlFragment("region = @p0", ["west"])),
            new StubFieldMaskSource([]));
        var reader = CreateBoundReader(factory, readSecurity);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("row-level security", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redshift", exception.Message, StringComparison.Ordinal);
        Assert.False(factory.WasCalled);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithFieldMaskResolved_RefusesReadBeforeConnecting(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var readSecurity = new LayerReadSecurityResolver(
            v2Provider: null,
            filterExpressionService: null,
            new StubRowFilterSource(null),
            new StubFieldMaskSource(["name"]));
        var reader = CreateBoundReader(factory, readSecurity);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("field-mask", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redshift", exception.Message, StringComparison.Ordinal);
        Assert.False(factory.WasCalled);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithPermanentFilterConfigured_RefusesReadBeforeConnecting(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var readSecurity = new LayerReadSecurityResolver(
            v2Provider: null,
            filterExpressionService: null,
            new StubRowFilterSource(null),
            new StubFieldMaskSource([]));
        var reader = CreateBoundReader(factory, readSecurity, permanentFilterExpression: "status = 'active'");

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("permanent", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redshift", exception.Message, StringComparison.Ordinal);
        Assert.False(factory.WasCalled);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithNoPolicyResolved_ReadsAsBefore(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var rowSource = new StubRowFilterSource(null);
        var maskSource = new StubFieldMaskSource([]);
        var readSecurity = new LayerReadSecurityResolver(v2Provider: null, filterExpressionService: null, rowSource, maskSource);
        var reader = CreateBoundReader(factory, readSecurity);

        // No policy resolves, so the read proceeds to the connection factory exactly as it
        // does without the resolver.
        await Assert.ThrowsAsync<RecordingConnectionFactory.SentinelException>(() => InvokeReadAsync(reader, operation));

        Assert.True(factory.WasCalled);
        Assert.Equal("res-parcels", rowSource.LastResourceId);
        Assert.Equal("res-parcels", maskSource.LastResourceId);
    }

    private static IFeatureReader CreateBoundReader(
        IRedshiftConnectionFactory factory,
        LayerReadSecurityResolver readSecurity,
        string? permanentFilterExpression = null)
    {
        var provider = CreateStore(factory, readSecurity);
        var binding = CreateBinding(provider, connection: null);
        if (permanentFilterExpression is not null)
        {
            binding = binding with
            {
                Resource = binding.Resource with
                {
                    PermanentFilter = new MetadataV2PermanentFilter
                    {
                        Expression = permanentFilterExpression,
                        Language = MetadataV2PermanentFilterLanguages.GeoServicesSql
                    }
                }
            };
        }

        return ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(binding);
    }

    private static Task InvokeReadAsync(IFeatureReader reader, string operation) => operation switch
    {
        "get" => reader.GetAsync(LayerId, 1),
        "query" => reader.QueryAsync(LayerId, new FeatureQuery()),
        "ids" => reader.QueryObjectIdsAsync(LayerId, new FeatureQuery()),
        "count" => reader.CountAsync(LayerId, new FeatureQuery()),
        "extent" => reader.GetExtentAsync(LayerId),
        "estimates" => reader.GetEstimatesAsync(LayerId),
        "statistics" => reader.QueryStatisticsAsync(LayerId, new FeatureQuery()),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private static FeatureProviderBinding CreateBinding(RedshiftFeatureStore provider, DataConnection? connection)
        => Parcels.CreateBinding(provider, connection);

    private static RedshiftFeatureStore CreateStore(
        IRedshiftConnectionFactory factory,
        LayerReadSecurityResolver? readSecurity)
    {
        var dataAccess = new RedshiftFeatureDataAccess(
            factory,
            Options.Create(new RedshiftOptions()),
            NullLogger<RedshiftFeatureDataAccess>.Instance);

        return new RedshiftFeatureStore(dataAccess, readSecurity);
    }

    private sealed class RecordingConnectionFactory : IRedshiftConnectionFactory
    {
        public bool WasCalled { get; private set; }

        public Task<NpgsqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new SentinelException();
        }

        public sealed class SentinelException : Exception
        {
        }
    }
}
