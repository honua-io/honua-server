// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.Versioning;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Import.FileImport.Resumable;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.AgentWorkspace;

// Private host configuration. The agent/task request cannot select roots, Redis
// identities, accounting policy, or an alternative source storage provider.
internal sealed record DatasetImportLocalStorageOptions(string RootDirectory, string DomainId,
    ImportStorageBudgetLimits Limits, int JournalCapacity, long MaximumScratchBytes, long MaximumFileBytes);

// Composes the physical domain before admitting any upload writers. Native
// processing, durable worker readiness and commit authority are separate checks;
// this component alone must never advertise import acceptance.
internal sealed class DatasetImportLocalStorage : IHostedService
{
    private bool _initialized;

    public DatasetImportLocalStorage(DatasetImportLocalStorageOptions options, IConnectionMultiplexer redis,
        ILoggerFactory loggerFactory, IUploadProgressStore progress)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The local import storage profile requires Linux filesystem durability.");
        if (string.IsNullOrWhiteSpace(options.RootDirectory) || !Path.IsPathFullyQualified(options.RootDirectory) ||
            options.MaximumScratchBytes < 1 || options.MaximumFileBytes < 1)
            throw new ArgumentException("Local import storage requires an absolute private root and explicit transfer limits.", nameof(options));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        if (root == Path.GetPathRoot(root)) throw new ArgumentException("A filesystem root cannot be an import storage domain.", nameof(options));
        RequirePrivateDirectory(root);
        var roots = new Dictionary<ImportStorageRootKind, string>
        {
            [ImportStorageRootKind.ProviderObjects] = Path.Join(root, "objects"),
            [ImportStorageRootKind.SourceScratch] = Path.Join(root, "source"),
            [ImportStorageRootKind.ProcessingScratch] = Path.Join(root, "processing")
        };
        foreach (var path in roots.Values) RequirePrivateDirectory(path);
        Domain = new(redis, options.DomainId, options.Limits, Path.Join(root, "control"), roots, options.JournalCapacity);
        Storage = new(Options.Create(new LocalStorageOptions
        {
            BasePath = roots[ImportStorageRootKind.ProviderObjects],
            CreateDirectoryIfNotExists = false
        }), loggerFactory.CreateLogger<LocalFileStorage>(), progress, Domain);
        Source = new(Storage);
        Scratch = new(roots[ImportStorageRootKind.SourceScratch], Domain, Domain);
        Processing = new(roots[ImportStorageRootKind.ProcessingScratch], Domain, Domain);
        Transport = new(roots[ImportStorageRootKind.SourceScratch], options.MaximumScratchBytes, options.MaximumFileBytes);
    }

    internal LocalImportStorageDomain Domain { get; }
    internal LocalFileStorage Storage { get; }
    internal ImportStagedSourceStorage Source { get; }
    internal ImportScratchStorage Scratch { get; }
    internal ImportProcessingScratchFactory Processing { get; }
    internal DatasetImportUploadTransportOptions Transport { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _initialized, false);
        await Domain.InitializeAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _initialized, true);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _initialized, false);
        return Task.CompletedTask;
    }

    internal async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!Volatile.Read(ref _initialized)) return false;
        try
        {
            await using var held = await Domain.EnterMutationAsync(cancellationToken).ConfigureAwait(false);
            return Volatile.Read(ref _initialized);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or RedisException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void RequirePrivateDirectory(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Import storage cannot use an aliased or non-directory ancestor.");
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
        }
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Import storage must be private to its host identity.");
        // Newly created ancestors must survive a restart along with the journal
        // and data beneath them. Sync each ancestor, including the filesystem root.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            LocalStorageDurability.FlushDirectoryTree(current, current);
    }
}

internal static class DatasetImportLocalStorageRegistration
{
    internal static void AddDatasetImportLocalStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("AgentWorkspace:DatasetImport:LocalStorage");
        if (!section.GetValue<bool>("Enabled")) return;
        var options = new DatasetImportLocalStorageOptions(section["RootDirectory"] ?? string.Empty,
            section["DomainId"] ?? string.Empty,
            new(section.GetValue<long>("GlobalBytes"), section.GetValue<long>("TenantBytes"),
                section.GetValue<long>("OwnerBytes"), section.GetValue<int>("MaximumObjects")),
            section.GetValue<int>("JournalCapacity"), section.GetValue<long>("MaximumScratchBytes"), section.GetValue<long>("MaximumFileBytes"));
        services.AddSingleton(options);
        services.AddSingleton<DatasetImportLocalStorage>();
        services.AddHostedService(provider => provider.GetRequiredService<DatasetImportLocalStorage>());
        services.AddSingleton(provider => provider.GetRequiredService<DatasetImportLocalStorage>().Source);
        services.AddSingleton<IImportStorageBudget>(provider => provider.GetRequiredService<DatasetImportLocalStorage>().Domain);
        services.AddSingleton(provider => provider.GetRequiredService<DatasetImportLocalStorage>().Scratch);
        services.AddSingleton<IImportProcessingScratchFactory>(provider => provider.GetRequiredService<DatasetImportLocalStorage>().Processing);
        services.AddSingleton(provider => provider.GetRequiredService<DatasetImportLocalStorage>().Transport);
        services.AddSingleton<RedisImportUploadStore>();
        services.AddScoped<IDatasetImportContributorAuthorizer, DatasetImportContributorAuthorizer>();
        services.AddScoped(provider => new ImportUploadTaskAuthority(provider.GetRequiredService<IDatasetImportTaskProvider>(),
            provider.GetRequiredService<IDatasetImportContributorAuthorizer>()));
        services.AddScoped<ImportUploadCoordinator>();
        services.AddDatasetImportLocalExecution();
    }
}
