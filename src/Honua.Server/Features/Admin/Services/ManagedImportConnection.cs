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
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ManagedImportConnection).FullName!);
            if (logger is not null)
            {
                Log.LeftUnchanged(logger, exception);
            }
        }
    }

    private static async Task EnsureCoreAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var registry = services.GetService<ISecureConnectionRegistry>();
        var encryption = services.GetService<IConnectionEncryptionService>();
        var configuration = services.GetService<IConfiguration>();
        if (registry is null || encryption is null || configuration is null)
        {
            return;
        }

        var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
            configuration,
            services.GetService<IConnectionSecretResolver>(),
            cancellationToken).ConfigureAwait(false);
        if (!TryRead(connectionString, out var target))
        {
            return;
        }

        var existing = await registry.GetConnectionAsync(Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var name = await ChooseNameAsync(registry, encryption, connectionString!, cancellationToken).ConfigureAwait(false);
            if (name is null)
            {
                return;
            }

            await CreateAsync(registry, encryption, connectionString!, target, name, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await StoresAsync(encryption, existing, connectionString!, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await RefreshAsync(registry, encryption, existing, connectionString!, target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateAsync(
        ISecureConnectionRegistry registry,
        IConnectionEncryptionService encryption,
        string connectionString,
        ImportDatabase target,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await BuildAsync(encryption, connectionString, target, name, cancellationToken).ConfigureAwait(false);
        try
        {
            await registry.CreateConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (await registry.GetConnectionAsync(Id, cancellationToken).ConfigureAwait(false) is null)
            {
                throw;
            }
        }
    }

    private static async Task RefreshAsync(
        ISecureConnectionRegistry registry,
        IConnectionEncryptionService encryption,
        DataConnection existing,
        string connectionString,
        ImportDatabase target,
        CancellationToken cancellationToken)
    {
        var encrypted = await encryption.EncryptConnectionStringAsync(connectionString).ConfigureAwait(false);
        existing.Host = target.Host;
        existing.Port = target.Port;
        existing.DatabaseName = target.DatabaseName;
        existing.Username = target.Username;
        existing.SslRequired = target.SslRequired;
        existing.SslMode = target.SslMode;
        existing.EncryptedConnectionString = encrypted;
        existing.EncryptionKeyVersion = await encryption.GetCurrentKeyVersionAsync().ConfigureAwait(false);
        existing.IsEncrypted = true;
        existing.SecretRef = null;
        existing.SecretType = null;
        await registry.UpdateConnectionAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DataConnection> BuildAsync(
        IConnectionEncryptionService encryption,
        string connectionString,
        ImportDatabase target,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name,
            target.Host,
            target.Port,
            target.DatabaseName,
            target.Username,
            await encryption.EncryptConnectionStringAsync(connectionString).ConfigureAwait(false),
            await encryption.GetCurrentKeyVersionAsync().ConfigureAwait(false),
            CreatedBy,
            Description,
            target.SslRequired,
            target.SslMode);
        connection.ConnectionId = Id;
        connection.Id = Id.ToString("D");
        return connection;
    }

    private static async Task<bool> StoresAsync(
        IConnectionEncryptionService encryption,
        DataConnection existing,
        string connectionString,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var stored = await encryption.DecryptConnectionStringAsync(
                existing.EncryptedConnectionString,
                existing.EncryptionKeyVersion).ConfigureAwait(false);
            return string.Equals(stored, connectionString, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool TryRead(string? connectionString, out ImportDatabase target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(builder.Host) ||
            builder.Host.Length > 255 ||
            string.IsNullOrWhiteSpace(builder.Database) ||
            builder.Database.Length > 64 ||
            string.IsNullOrWhiteSpace(builder.Username) ||
            builder.Username.Length > 64 ||
            builder.Port is < 1 or > 65535)
        {
            return false;
        }

        var sslMode = MapSsl(builder.SslMode);
        target = new ImportDatabase(
            builder.Host,
            builder.Port,
            builder.Database,
            builder.Username,
            sslMode is StoreSslMode.Require or StoreSslMode.VerifyCa or StoreSslMode.VerifyFull,
            sslMode);
        return true;
    }

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

    private static async Task<string?> ChooseNameAsync(
        ISecureConnectionRegistry registry,
        IConnectionEncryptionService encryption,
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (await RepresentsServerAsync(registry, encryption, Name, connectionString, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var named = await registry.GetConnectionByNameAsync(Name, cancellationToken).ConfigureAwait(false);
        if (named is null || named.ConnectionId == Id)
        {
            return Name;
        }

        if (await RepresentsServerAsync(registry, encryption, FallbackName, connectionString, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var fallback = await registry.GetConnectionByNameAsync(FallbackName, cancellationToken).ConfigureAwait(false);
        return fallback is null || fallback.ConnectionId == Id ? FallbackName : null;
    }

    private static async Task<bool> RepresentsServerAsync(
        ISecureConnectionRegistry registry,
        IConnectionEncryptionService encryption,
        string name,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var named = await registry.GetConnectionByNameAsync(name, cancellationToken).ConfigureAwait(false);
        return named is not null &&
            named.ConnectionId != Id &&
            await StoresAsync(encryption, named, connectionString, cancellationToken).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4490, Level = LogLevel.Warning, Message = "The imported-data connection was left unchanged.")]
        public static partial void LeftUnchanged(ILogger logger, Exception exception);
    }

    private readonly record struct ImportDatabase(
        string Host,
        int Port,
        string DatabaseName,
        string Username,
        bool SslRequired,
        StoreSslMode SslMode);
}
