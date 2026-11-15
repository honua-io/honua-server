// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using StoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Server.Features.Admin.Services;

internal static partial class ManagedImportConnection
{
    internal static readonly Guid Id = new("77460000-0000-4000-8000-000000007746");
    internal const string Name = "Imported data";
    internal const string FallbackName = "Imported data (built-in)";
    internal const string Description = "Tables imported into this server.";
    internal const string CreatedBy = "server";

    internal static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            await EnsureCoreAsync(services, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ManagedImportConnection).FullName!);
                if (logger is not null)
                {
                    Log.LeftUnchanged(logger, exception);
                }
            }
            catch (Exception logFailure) when (logFailure is not OperationCanceledException)
            {
                // A logger failure must not turn the connections list into an error.
            }
        }
    }

    private static async Task EnsureCoreAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var work = await PrepareAsync(services, cancellationToken).ConfigureAwait(false);
        if (work is null)
        {
            return;
        }

        var existing = await work.Registry.GetConnectionAsync(Id, work.CancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateWhenNamedAsync(work).ConfigureAwait(false);
            return;
        }

        if (await StoresAsync(work, existing).ConfigureAwait(false))
        {
            return;
        }

        await RefreshAsync(work, existing).ConfigureAwait(false);
    }

    private static async Task<EnsureWork?> PrepareAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var registry = services.GetService<ISecureConnectionRegistry>();
        var encryption = services.GetService<IConnectionEncryptionService>();
        var configuration = services.GetService<IConfiguration>();
        if (registry is null || encryption is null || configuration is null)
        {
            return null;
        }

        var connectionString = await ResolveServerConnectionStringAsync(services, configuration, cancellationToken)
            .ConfigureAwait(false);
        var target = ReadDatabase(connectionString);
        if (target is null || connectionString is null)
        {
            return null;
        }

        return new EnsureWork
        {
            Registry = registry,
            Encryption = encryption,
            ConnectionString = connectionString,
            Target = target.Value,
            CancellationToken = cancellationToken,
        };
    }

    private static async Task CreateWhenNamedAsync(EnsureWork work)
    {
        var name = await ChooseNameAsync(work).ConfigureAwait(false);
        if (name is not null)
        {
            await CreateAsync(work, name).ConfigureAwait(false);
        }
    }

    private static async Task CreateAsync(EnsureWork work, string name)
    {
        var connection = await BuildAsync(work, name).ConfigureAwait(false);
        try
        {
            await work.Registry.CreateConnectionAsync(connection, work.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (await work.Registry.GetConnectionAsync(Id, work.CancellationToken).ConfigureAwait(false) is null)
            {
                throw;
            }
        }
    }

    private static async Task RefreshAsync(EnsureWork work, DataConnection existing)
    {
        var encrypted = await work.Encryption.EncryptConnectionStringAsync(work.ConnectionString).ConfigureAwait(false);
        var target = work.Target;
        existing.Host = target.Host;
        existing.Port = target.Address.Port;
        existing.DatabaseName = target.Address.DatabaseName;
        existing.Username = target.Address.Username;
        existing.SslRequired = RequiresSsl(target.SslMode);
        existing.SslMode = target.SslMode;
        existing.EncryptedConnectionString = encrypted;
        existing.EncryptionKeyVersion = await work.Encryption.GetCurrentKeyVersionAsync().ConfigureAwait(false);
        existing.IsEncrypted = true;
        existing.SecretRef = null;
        existing.SecretType = null;
        await work.Registry.UpdateConnectionAsync(existing, work.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<DataConnection> BuildAsync(EnsureWork work, string name)
    {
        work.CancellationToken.ThrowIfCancellationRequested();
        var target = work.Target;
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name,
            target.Host,
            target.Address.Port,
            target.Address.DatabaseName,
            target.Address.Username,
            await work.Encryption.EncryptConnectionStringAsync(work.ConnectionString).ConfigureAwait(false),
            await work.Encryption.GetCurrentKeyVersionAsync().ConfigureAwait(false),
            CreatedBy,
            Description,
            RequiresSsl(target.SslMode),
            target.SslMode);
        connection.ConnectionId = Id;
        connection.Id = Id.ToString("D");
        return connection;
    }

    private static async Task<bool> StoresAsync(EnsureWork work, DataConnection existing)
    {
        work.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            var stored = await work.Encryption.DecryptConnectionStringAsync(
                existing.EncryptedConnectionString,
                existing.EncryptionKeyVersion).ConfigureAwait(false);
            return string.Equals(stored, work.ConnectionString, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static ImportDatabase? ReadDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = Builder(connectionString);
        if (builder is null)
        {
            return null;
        }

        var host = builder.Host;
        var database = builder.Database;
        var username = builder.Username;
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > 255 ||
            string.IsNullOrWhiteSpace(database) ||
            database.Length > 64 ||
            string.IsNullOrWhiteSpace(username) ||
            username.Length > 64 ||
            builder.Port is < 1 or > 65535)
        {
            return null;
        }

        return new ImportDatabase(host, new DatabaseAddress(builder.Port, database, username), MapSsl(builder.SslMode));
    }

    private static NpgsqlConnectionStringBuilder? Builder(string connectionString)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool RequiresSsl(StoreSslMode mode) =>
        mode is StoreSslMode.Require or StoreSslMode.VerifyCa or StoreSslMode.VerifyFull;

    private static StoreSslMode MapSsl(Npgsql.SslMode mode) => mode switch
    {
        Npgsql.SslMode.Disable => StoreSslMode.Disable,
        Npgsql.SslMode.Allow => StoreSslMode.Allow,
        Npgsql.SslMode.Prefer => StoreSslMode.Prefer,
        Npgsql.SslMode.Require => StoreSslMode.Require,
        Npgsql.SslMode.VerifyCA => StoreSslMode.VerifyCa,
        Npgsql.SslMode.VerifyFull => StoreSslMode.VerifyFull,
        _ => StoreSslMode.Prefer
    };

    private static async Task<string?> ChooseNameAsync(EnsureWork work)
    {
        if (await RepresentsServerAsync(work, Name).ConfigureAwait(false) ||
            await RepresentsServerAsync(work, FallbackName).ConfigureAwait(false))
        {
            return null;
        }

        var named = await work.Registry.GetConnectionByNameAsync(Name, work.CancellationToken).ConfigureAwait(false);
        if (named is null || named.ConnectionId == Id)
        {
            return Name;
        }

        var fallback = await work.Registry.GetConnectionByNameAsync(FallbackName, work.CancellationToken).ConfigureAwait(false);
        return fallback is null || fallback.ConnectionId == Id ? FallbackName : null;
    }

    // The running data source is resolved once at startup. Reading configuration again can
    // follow a reload to a different database than the one imports already use.
    private static async Task<string?> ResolveServerConnectionStringAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var running = services.GetService<NpgsqlDataSource>()?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(running))
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(running);
                if (!string.IsNullOrWhiteSpace(builder.Password))
                {
                    return running;
                }
            }
            catch (ArgumentException)
            {
                // A data source string that cannot be read falls through to configuration.
            }
        }

        return await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
            configuration,
            services.GetService<IConnectionSecretResolver>(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RepresentsServerAsync(EnsureWork work, string name)
    {
        var named = await work.Registry.GetConnectionByNameAsync(name, work.CancellationToken).ConfigureAwait(false);
        return named is not null &&
            named.ConnectionId != Id &&
            await StoresAsync(work, named).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4490, Level = LogLevel.Warning, Message = "The imported-data connection was left unchanged.")]
        public static partial void LeftUnchanged(ILogger logger, Exception exception);
    }

    private sealed class EnsureWork
    {
        public required ISecureConnectionRegistry Registry { get; init; }
        public required IConnectionEncryptionService Encryption { get; init; }
        public required string ConnectionString { get; init; }
        public required ImportDatabase Target { get; init; }
        public required CancellationToken CancellationToken { get; init; }
    }

    private readonly record struct DatabaseAddress(int Port, string DatabaseName, string Username);

    private readonly record struct ImportDatabase(string Host, DatabaseAddress Address, StoreSslMode SslMode);
}
