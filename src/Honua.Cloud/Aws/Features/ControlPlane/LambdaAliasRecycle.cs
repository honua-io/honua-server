// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace Honua.ControlPlane;

/// <summary>
/// Recycles a published Lambda alias without substituting a different image or configuration.
/// Lambda has no restart API, and publishing an unchanged function returns the current version.
/// The executor sets only the function description so the next publish is a new version, and
/// moves the alias only when that version still matches the serving workload.
/// </summary>
internal interface ILambdaAliasRecycle
{
    /// <summary>Returns the image digest that remains on the alias.</summary>
    Task<string> RecycleAsync(
        string functionName,
        string aliasName,
        string? region,
        string? serviceUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>Request bodies for a Lambda alias recycle. The description write carries no other configuration.</summary>
internal static class LambdaAliasRecycleRequests
{
    public static UpdateFunctionConfigurationRequest ForceNewRevision(string functionName, string description)
        => new()
        {
            FunctionName = functionName,
            Description = description
        };

    public static PublishVersionRequest Publish(string functionName)
        => new() { FunctionName = functionName };

    public static UpdateAliasRequest MoveAlias(string functionName, string aliasName, string functionVersion, string revisionId)
        => new()
        {
            FunctionName = functionName,
            Name = aliasName,
            FunctionVersion = functionVersion,
            RevisionId = revisionId
        };
}

/// <summary>Fails closed unless the image reference is unchanged. The alias is not moved when this throws.</summary>
internal static class LambdaImageGuard
{
    public const string ChangedMessage = "Lambda restart changed the function image. The alias was not moved.";

    public static string RequireImmutable(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.IndexOf("@sha256:", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Lambda restart requires an immutable image. The alias was not moved.");
        }

        return image;
    }

    public static string RequireUnchanged(string before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ChangedMessage);
        }

        return before;
    }
}

/// <summary>
/// Serving workload of one Lambda version. Description is omitted: it is the only field a restart may change.
/// </summary>
internal sealed record LambdaWorkloadSnapshot
{
    public required string Image { get; init; }

    public string? PackageType { get; init; }

    public string? Role { get; init; }

    public int? Timeout { get; init; }

    public int? MemorySize { get; init; }

    public int? EphemeralStorageMb { get; init; }

    public string? Handler { get; init; }

    public string? Runtime { get; init; }

    public string? KmsKeyArn { get; init; }

    public string? DeadLetterTargetArn { get; init; }

    public string? TracingMode { get; init; }

    public string? VpcId { get; init; }

    public bool? Ipv6AllowedForDualStack { get; init; }

    public IReadOnlyList<string> SubnetIds { get; init; } = [];

    public IReadOnlyList<string> SecurityGroupIds { get; init; } = [];

    public IReadOnlyList<string> Architectures { get; init; } = [];

    public IReadOnlyList<string> LayerArns { get; init; } = [];

    public IReadOnlyList<string> FileSystems { get; init; } = [];

    public IReadOnlyList<string> ImageCommand { get; init; } = [];

    public IReadOnlyList<string> ImageEntryPoint { get; init; } = [];

    public string? ImageWorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? SnapStart { get; init; }

    public string? LogGroup { get; init; }

    public string? LogFormat { get; init; }

    public string? ApplicationLogLevel { get; init; }

    public string? SystemLogLevel { get; init; }

    public string? RuntimeVersionArn { get; init; }

    public int? DurableExecutionTimeout { get; init; }

    public int? DurableRetentionDays { get; init; }

    public string? TenantIsolationMode { get; init; }

    public string? CapacityProviderArn { get; init; }

    public double? CapacityProviderMemoryGiBPerVCpu { get; init; }

    public int? CapacityProviderMaxConcurrency { get; init; }

    public string? Description { get; init; }

    public static LambdaWorkloadSnapshot Capture(string? image, GetFunctionConfigurationResponse configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Environment?.Error is not null)
        {
            throw new InvalidOperationException("Lambda environment could not be read. The alias was not moved.");
        }

        var vpc = configuration.VpcConfig;
        var imageConfig = configuration.ImageConfigResponse?.ImageConfig;
        var capacity = configuration.CapacityProviderConfig?.LambdaManagedInstancesCapacityProviderConfig;
        return new LambdaWorkloadSnapshot
        {
            Image = image ?? string.Empty,
            PackageType = configuration.PackageType?.Value,
            Role = configuration.Role,
            Timeout = configuration.Timeout,
            MemorySize = configuration.MemorySize,
            EphemeralStorageMb = configuration.EphemeralStorage?.Size,
            Handler = configuration.Handler,
            Runtime = configuration.Runtime?.Value,
            KmsKeyArn = configuration.KMSKeyArn,
            DeadLetterTargetArn = configuration.DeadLetterConfig?.TargetArn,
            TracingMode = configuration.TracingConfig?.Mode?.Value,
            VpcId = vpc?.VpcId,
            Ipv6AllowedForDualStack = vpc?.Ipv6AllowedForDualStack,
            SubnetIds = Sorted(vpc?.SubnetIds),
            SecurityGroupIds = Sorted(vpc?.SecurityGroupIds),
            Architectures = Sorted(configuration.Architectures),
            LayerArns = Sorted(configuration.Layers?.Select(static item => item.Arn)),
            FileSystems = Sorted(configuration.FileSystemConfigs?.Select(static item => $"{item.Arn}|{item.LocalMountPath}")),
            ImageCommand = Listed(imageConfig?.Command),
            ImageEntryPoint = Listed(imageConfig?.EntryPoint),
            ImageWorkingDirectory = imageConfig?.WorkingDirectory,
            Environment = CopyEnvironment(configuration.Environment?.Variables),
            SnapStart = configuration.SnapStart?.ApplyOn?.Value,
            LogGroup = configuration.LoggingConfig?.LogGroup,
            LogFormat = configuration.LoggingConfig?.LogFormat?.Value,
            ApplicationLogLevel = configuration.LoggingConfig?.ApplicationLogLevel?.Value,
            SystemLogLevel = configuration.LoggingConfig?.SystemLogLevel?.Value,
            RuntimeVersionArn = configuration.RuntimeVersionConfig?.RuntimeVersionArn,
            DurableExecutionTimeout = configuration.DurableConfig?.ExecutionTimeout,
            DurableRetentionDays = configuration.DurableConfig?.RetentionPeriodInDays,
            TenantIsolationMode = configuration.TenancyConfig?.TenantIsolationMode?.Value,
            CapacityProviderArn = capacity?.CapacityProviderArn,
            CapacityProviderMemoryGiBPerVCpu = capacity?.ExecutionEnvironmentMemoryGiBPerVCpu,
            CapacityProviderMaxConcurrency = capacity?.PerExecutionEnvironmentMaxConcurrency,
            Description = configuration.Description
        };
    }

    private static string[] Sorted(IEnumerable<string?>? values)
        => values is null
            ? []
            : values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!.Trim())
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

    private static string[] Listed(IEnumerable<string>? values)
        => values?.Where(static value => value is not null).ToArray() ?? [];

    private static Dictionary<string, string> CopyEnvironment(IDictionary<string, string>? variables)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (variables is null)
        {
            return copy;
        }

        foreach (var pair in variables)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                copy[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        return copy;
    }
}

/// <summary>Compares two Lambda versions without treating the restart description as workload drift.</summary>
internal static class LambdaWorkloadIdentity
{
    public const string DivergedMessage =
        "Lambda restart refused because unpublished configuration differs from the serving version. The alias was not moved.";

    public const string SameVersionMessage =
        "Lambda restart did not publish a new version. The alias was not moved.";

    public static bool Same(LambdaWorkloadSnapshot serving, LambdaWorkloadSnapshot latest)
        => string.Equals(serving.Image, latest.Image, StringComparison.Ordinal)
           && string.Equals(serving.PackageType, latest.PackageType, StringComparison.Ordinal)
           && string.Equals(serving.Role, latest.Role, StringComparison.Ordinal)
           && serving.Timeout == latest.Timeout
           && serving.MemorySize == latest.MemorySize
           && serving.EphemeralStorageMb == latest.EphemeralStorageMb
           && string.Equals(serving.Handler, latest.Handler, StringComparison.Ordinal)
           && string.Equals(serving.Runtime, latest.Runtime, StringComparison.Ordinal)
           && string.Equals(serving.KmsKeyArn, latest.KmsKeyArn, StringComparison.Ordinal)
           && string.Equals(serving.DeadLetterTargetArn, latest.DeadLetterTargetArn, StringComparison.Ordinal)
           && string.Equals(serving.TracingMode, latest.TracingMode, StringComparison.Ordinal)
           && string.Equals(serving.VpcId, latest.VpcId, StringComparison.Ordinal)
           && serving.Ipv6AllowedForDualStack == latest.Ipv6AllowedForDualStack
           && SameList(serving.SubnetIds, latest.SubnetIds)
           && SameList(serving.SecurityGroupIds, latest.SecurityGroupIds)
           && SameList(serving.Architectures, latest.Architectures)
           && SameList(serving.LayerArns, latest.LayerArns)
           && SameList(serving.FileSystems, latest.FileSystems)
           && SameList(serving.ImageCommand, latest.ImageCommand)
           && SameList(serving.ImageEntryPoint, latest.ImageEntryPoint)
           && string.Equals(serving.ImageWorkingDirectory, latest.ImageWorkingDirectory, StringComparison.Ordinal)
           && SameMap(serving.Environment, latest.Environment)
           && string.Equals(serving.SnapStart, latest.SnapStart, StringComparison.Ordinal)
           && string.Equals(serving.LogGroup, latest.LogGroup, StringComparison.Ordinal)
           && string.Equals(serving.LogFormat, latest.LogFormat, StringComparison.Ordinal)
           && string.Equals(serving.ApplicationLogLevel, latest.ApplicationLogLevel, StringComparison.Ordinal)
           && string.Equals(serving.SystemLogLevel, latest.SystemLogLevel, StringComparison.Ordinal)
           && string.Equals(serving.RuntimeVersionArn, latest.RuntimeVersionArn, StringComparison.Ordinal)
           && serving.DurableExecutionTimeout == latest.DurableExecutionTimeout
           && serving.DurableRetentionDays == latest.DurableRetentionDays
           && string.Equals(serving.TenantIsolationMode, latest.TenantIsolationMode, StringComparison.Ordinal)
           && string.Equals(serving.CapacityProviderArn, latest.CapacityProviderArn, StringComparison.Ordinal)
           && serving.CapacityProviderMemoryGiBPerVCpu == latest.CapacityProviderMemoryGiBPerVCpu
           && serving.CapacityProviderMaxConcurrency == latest.CapacityProviderMaxConcurrency;

    public static bool IsNewVersion(string aliasVersion, string? publishedVersion)
        => !string.IsNullOrWhiteSpace(publishedVersion)
           && long.TryParse(publishedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var published)
           && published > 0
           && !string.Equals(aliasVersion, publishedVersion, StringComparison.Ordinal);

    private static bool SameList(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameMap(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record LambdaAliasView(string? FunctionVersion, string? Image, string? RevisionId);

internal interface ILambdaFunctionControl
{
    Task<LambdaAliasView> ReadAliasAsync(string functionName, string aliasName, CancellationToken cancellationToken);

    Task<LambdaWorkloadSnapshot> ReadAsync(string functionName, string qualifier, CancellationToken cancellationToken);

    Task UpdateDescriptionAsync(string functionName, string description, CancellationToken cancellationToken);

    Task<string> PublishAsync(string functionName, CancellationToken cancellationToken);

    Task MoveAliasAsync(string functionName, string aliasName, string functionVersion, string revisionId, CancellationToken cancellationToken);
}

/// <summary>Decides the recycle. A configuration mismatch throws before the description write.</summary>
internal static class LambdaAliasRecycleExecution
{
    public static async Task<string> ExecuteAsync(
        ILambdaFunctionControl control,
        string functionName,
        string aliasName,
        string revisionDescription,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        var alias = await control.ReadAliasAsync(functionName, aliasName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(alias.FunctionVersion) ||
            !long.TryParse(alias.FunctionVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentVersion) ||
            currentVersion <= 0)
        {
            throw new InvalidOperationException(
                "Lambda restart requires a published numeric alias version. The alias was not moved.");
        }

        if (string.IsNullOrWhiteSpace(alias.RevisionId))
        {
            throw new InvalidOperationException("Lambda restart requires an alias revision ID. The alias was not moved.");
        }

        var image = LambdaImageGuard.RequireImmutable(alias.Image);
        var serving = await control.ReadAsync(functionName, alias.FunctionVersion, cancellationToken).ConfigureAwait(false);
        var latest = await control.ReadAsync(functionName, "$LATEST", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(serving.PackageType, PackageType.Image.Value, StringComparison.Ordinal) ||
            !string.Equals(serving.Image, image, StringComparison.Ordinal) ||
            !LambdaWorkloadIdentity.Same(serving, latest))
        {
            throw new InvalidOperationException(LambdaWorkloadIdentity.DivergedMessage);
        }

        try
        {
            await control.UpdateDescriptionAsync(functionName, revisionDescription, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RestoreAsync(control, functionName, latest.Description, cancellationToken).ConfigureAwait(false);
            throw;
        }

        try
        {
            var published = await control.PublishAsync(functionName, cancellationToken).ConfigureAwait(false);
            if (!LambdaWorkloadIdentity.IsNewVersion(alias.FunctionVersion, published))
            {
                await RestoreAsync(control, functionName, latest.Description, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(LambdaWorkloadIdentity.SameVersionMessage);
            }

            LambdaWorkloadSnapshot publishedSnapshot;
            try
            {
                publishedSnapshot = await control.ReadAsync(functionName, published, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await RestoreAsync(control, functionName, latest.Description, cancellationToken).ConfigureAwait(false);
                throw;
            }
            if (!string.Equals(image, publishedSnapshot.Image, StringComparison.Ordinal))
            {
                await RestoreAsync(control, functionName, latest.Description, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(LambdaImageGuard.ChangedMessage);
            }

            if (!LambdaWorkloadIdentity.Same(serving, publishedSnapshot))
            {
                await RestoreAsync(control, functionName, latest.Description, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(LambdaWorkloadIdentity.DivergedMessage);
            }

            await control.MoveAliasAsync(functionName, aliasName, published, alias.RevisionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            await RestoreAsync(control, functionName, latest.Description, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var after = await control.ReadAliasAsync(functionName, aliasName, cancellationToken).ConfigureAwait(false);
        return LambdaImageGuard.RequireUnchanged(image, after.Image);
    }

    private static async Task RestoreAsync(
        ILambdaFunctionControl control,
        string functionName,
        string? description,
        CancellationToken cancellationToken)
    {
        try
        {
            await control.UpdateDescriptionAsync(functionName, description ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The alias was not moved. Putting the previous description back is best-effort.
        }
    }
}

/// <summary>Process-credential Lambda client for governed alias recycle.</summary>
internal sealed class AwsSdkLambdaAliasRecycle : ILambdaAliasRecycle
{
    public async Task<string> RecycleAsync(
        string functionName,
        string aliasName,
        string? region,
        string? serviceUrl,
        CancellationToken cancellationToken = default)
    {
        using var client = AwsSdkLambdaAliasClient.CreateClient(region, serviceUrl);
        var description = "honua-workload-restart:" + DateTimeOffset.UtcNow.ToString(
            "yyyyMMddHHmmssfffffff",
            CultureInfo.InvariantCulture);
        return await LambdaAliasRecycleExecution.ExecuteAsync(
                new SdkLambdaFunctionControl(client),
                functionName,
                aliasName,
                description,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? ImageOf(GetFunctionResponse response)
        => string.IsNullOrWhiteSpace(response.Code?.ResolvedImageUri)
            ? response.Code?.ImageUri
            : response.Code.ResolvedImageUri;

    private sealed class SdkLambdaFunctionControl(AmazonLambdaClient client) : ILambdaFunctionControl
    {
        public async Task<LambdaAliasView> ReadAliasAsync(
            string functionName,
            string aliasName,
            CancellationToken cancellationToken)
        {
            var alias = await client.GetAliasAsync(
                    new GetAliasRequest { FunctionName = functionName, Name = aliasName },
                    cancellationToken)
                .ConfigureAwait(false);
            var function = await client.GetFunctionAsync(
                    new GetFunctionRequest { FunctionName = functionName, Qualifier = aliasName },
                    cancellationToken)
                .ConfigureAwait(false);
            return new LambdaAliasView(alias.FunctionVersion, ImageOf(function), alias.RevisionId);
        }

        public async Task<LambdaWorkloadSnapshot> ReadAsync(
            string functionName,
            string qualifier,
            CancellationToken cancellationToken)
        {
            var function = await client.GetFunctionAsync(
                    new GetFunctionRequest { FunctionName = functionName, Qualifier = qualifier },
                    cancellationToken)
                .ConfigureAwait(false);
            var configuration = await client.GetFunctionConfigurationAsync(
                    new GetFunctionConfigurationRequest { FunctionName = functionName, Qualifier = qualifier },
                    cancellationToken)
                .ConfigureAwait(false);
            return LambdaWorkloadSnapshot.Capture(ImageOf(function), configuration);
        }

        public async Task UpdateDescriptionAsync(
            string functionName,
            string description,
            CancellationToken cancellationToken)
        {
            var updated = await client.UpdateFunctionConfigurationAsync(
                    LambdaAliasRecycleRequests.ForceNewRevision(functionName, description),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await WaitUntilSuccessfulAsync(client, functionName, updated.LastUpdateStatus, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Lambda restart did not publish the current image. The alias was not moved.");
            }
        }

        public async Task<string> PublishAsync(string functionName, CancellationToken cancellationToken)
        {
            var published = await client.PublishVersionAsync(
                    LambdaAliasRecycleRequests.Publish(functionName),
                    cancellationToken)
                .ConfigureAwait(false);
            return published.Version ?? string.Empty;
        }

        public Task MoveAliasAsync(
            string functionName,
            string aliasName,
            string functionVersion,
            string revisionId,
            CancellationToken cancellationToken)
            => client.UpdateAliasAsync(
                LambdaAliasRecycleRequests.MoveAlias(functionName, aliasName, functionVersion, revisionId),
                cancellationToken);
    }

    private static async Task<bool> WaitUntilSuccessfulAsync(
        AmazonLambdaClient client,
        string functionName,
        LastUpdateStatus? initial,
        CancellationToken cancellationToken)
    {
        const int attempts = 15;
        const int delayMilliseconds = 2000;
        var status = initial;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (IsSuccessful(status))
            {
                return true;
            }

            if (string.Equals(status?.Value, LastUpdateStatus.Failed.Value, StringComparison.Ordinal))
            {
                return false;
            }

            if (delayMilliseconds > 0 && (attempt > 0 || string.Equals(status?.Value, LastUpdateStatus.InProgress.Value, StringComparison.Ordinal)))
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            var configuration = await client.GetFunctionConfigurationAsync(
                    new GetFunctionConfigurationRequest { FunctionName = functionName },
                    cancellationToken)
                .ConfigureAwait(false);
            status = configuration.LastUpdateStatus;
        }

        return IsSuccessful(status);
    }

    private static bool IsSuccessful(LastUpdateStatus? status)
        => string.Equals(status?.Value, LastUpdateStatus.Successful.Value, StringComparison.Ordinal);
}
