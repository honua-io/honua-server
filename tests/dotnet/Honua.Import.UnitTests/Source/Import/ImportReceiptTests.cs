// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Import.FileImport;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

public sealed class ImportReceiptTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CompletedReceipt_RequiresUniqueActiveConnection(int matches)
    {
        var connections = Enumerable.Range(0, matches).Select(i => new DataConnection { Name = $"database-{i}" }).ToArray();
        var services = new ServiceCollection();
        services.AddSingleton<IDatabaseConnectionProvider>(new DatabaseProvider());
        services.AddSingleton<ISecureConnectionRegistry>(new Registry(connections));
        services.AddSingleton<ISecureConnectionResolver>(new Resolver());
        services.AddSingleton<ICatalogConnectionIdentityMatcher>(new Matcher());
        using var provider = services.BuildServiceProvider();
        var progress = ImportProgress.CreateInitial("receipt", "logical", SupportedFileFormat.GeoJson) with
        {
            Status = ImportStatus.Completed,
            Schema = "imports",
            PhysicalTableName = "physical"
        };

        var receipt = await ImportEndpoints.EnrichImportReceiptAsync(provider, progress, CancellationToken.None);

        receipt.Schema.Should().Be("imports");
        receipt.PhysicalTableName.Should().Be("physical");
        if (matches == 1)
        {
            receipt.ConnectionId.Should().Be(connections[0].ConnectionId.ToString());
            receipt.Warnings.Should().BeEmpty();
        }
        else
        {
            receipt.ConnectionId.Should().BeNull();
            receipt.Warnings.Should().ContainSingle().Which.Should().Contain("No unique active database connection");
        }
    }

    [Theory]
    [InlineData(ImportStatus.Queued)]
    [InlineData(ImportStatus.Processing)]
    [InlineData(ImportStatus.Failed)]
    [InlineData(ImportStatus.Cancelled)]
    public async Task IncompleteReceipt_DoesNotExposePublishableIdentifiers(ImportStatus status)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var progress = ImportProgress.CreateInitial("receipt", "logical", SupportedFileFormat.GeoJson) with
        {
            Status = status,
            Schema = "imports",
            PhysicalTableName = "physical",
            ConnectionId = "stale"
        };

        var receipt = await ImportEndpoints.EnrichImportReceiptAsync(services, progress, CancellationToken.None);

        receipt.Schema.Should().BeNull();
        receipt.PhysicalTableName.Should().BeNull();
        receipt.ConnectionId.Should().BeNull();
    }

    private sealed class Matcher : ICatalogConnectionIdentityMatcher
    {
        public bool IsMatch(string provider, string catalogConnectionString, string registeredConnectionString) => true;
    }

    private sealed class DatabaseProvider : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => "import database";
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Resolver : ISecureConnectionResolver
    {
        public Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult("registered database");
        public Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default) => Task.FromResult("registered database");
        public Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Registry(DataConnection[] connections) : ISecureConnectionRegistry
    {
        public Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DataConnection>>(connections);
        public Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RegisterConnectionAsync(DataConnection connection) => throw new NotSupportedException();
        public Task<DataConnection?> GetConnectionAsync(string connectionId) => throw new NotSupportedException();
        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IEnumerable<DataConnection>> GetAllConnectionsAsync() => throw new NotSupportedException();
        public Task<bool> RemoveConnectionAsync(string connectionId) => throw new NotSupportedException();
        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync() => throw new NotSupportedException();
        public Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
