// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// #6359: a calculate that spans more than one edit batch must not leave the earlier
/// batches committed when a later batch fails. A retry of an increment then adds one,
/// not two.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerCalculateBatchAtomicityTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public FeatureServerCalculateBatchAtomicityTests()
    {
        // The writer is scoped, so each request builds a new decorator. The gate has to
        // live outside that instance or the retry would fail its second batch too.
        var gate = new LaterBatchFailureGate();
        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureServices(services => services.PostConfigure<LimitsOptions>(options =>
                options.Edits.MaxFeaturesPerEdit = 1))
            .DecorateService<IFeatureWriter>(inner => new FailLaterCalculateBatchWriter(inner, gate));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(
            WebAppFixture.TestServiceId,
            ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_LaterBatchFailure_RetryOfIncrementIncreasesStoredValuesOnce()
    {
        var firstId = await InsertPopulationAsync("calc-6359-a", 10);
        var secondId = await InsertPopulationAsync("calc-6359-b", 20);
        var where = string.Create(CultureInfo.InvariantCulture, $"objectid IN ({firstId}, {secondId})");
        var calcExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "population", sqlExpression = "population + 1" }
        });

        var failed = await PostCalculateAsync(calcExpression, where);
        var failedBody = await failed.Content.ReadAsStringAsync();
        CalculateSucceeded(failedBody).Should().BeFalse(
            "a later batch failure must not report a successful calculate: " + failedBody);
        (await ReadPopulationAsync(firstId)).Should().Be(10);
        (await ReadPopulationAsync(secondId)).Should().Be(20);

        var retried = await PostCalculateAsync(calcExpression, where);
        var retriedBody = await retried.Content.ReadAsStringAsync();
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        CalculateSucceeded(retriedBody).Should().BeTrue("retry body: " + retriedBody);
        using (var document = JsonDocument.Parse(retriedBody))
        {
            document.RootElement.GetProperty("updatedFeatureCount").GetInt32().Should().Be(2);
        }

        (await ReadPopulationAsync(firstId)).Should().Be(11);
        (await ReadPopulationAsync(secondId)).Should().Be(21);
    }

    private async Task<HttpResponseMessage> PostCalculateAsync(string calcExpression, string where)
    {
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("calcExpression", calcExpression),
            new KeyValuePair<string, string>("where", where),
            new KeyValuePair<string, string>("f", "json"),
        });
        return await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate",
            form);
    }

    private async Task<long> InsertPopulationAsync(string name, int population)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, NULL, jsonb_build_object('name', @name::text, 'population', to_jsonb(@population::int)))
            RETURNING objectid;
            """;
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("population", population);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<decimal> ReadPopulationAsync(long objectId)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT attributes->>'population' FROM features WHERE layer_id = @layerId AND objectid = @objectId";
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("objectId", objectId);
        var text = await command.ExecuteScalarAsync() as string;
        return decimal.Parse(text ?? throw new InvalidOperationException("population was not stored."), CultureInfo.InvariantCulture);
    }

    private static bool CalculateSucceeded(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Fails the second apply of the first writer transaction opened in this host.
    /// Later transactions, including the client retry, pass through.
    /// </summary>
    private sealed class LaterBatchFailureGate
    {
        private int _transactions;

        public bool IsFirstTransaction() => Interlocked.Increment(ref _transactions) == 1;
    }

    private sealed class FailLaterCalculateBatchWriter(IFeatureWriter inner, LaterBatchFailureGate gate) : IFeatureWriter
    {
        public async Task<IFeatureWriterTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var transaction = await inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            return gate.IsFirstTransaction()
                ? new LaterBatchFailingTransaction(transaction)
                : transaction;
        }

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.CreateAsync(layerId, feature, cancellationToken);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(layerId, feature, cancellationToken);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(layerId, featureId, cancellationToken);

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => inner.ApplyEditsAsync(layerId, editBatch, cancellationToken);
    }

    private sealed class LaterBatchFailingTransaction(IFeatureWriterTransaction inner) : IFeatureWriterTransaction
    {
        private int _applies;

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _applies) == 2)
            {
                throw new InvalidOperationException("Injected later calculate batch failure.");
            }

            return inner.ApplyEditsAsync(layerId, editBatch, cancellationToken);
        }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => inner.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
