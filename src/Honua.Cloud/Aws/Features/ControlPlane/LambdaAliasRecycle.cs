// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace Honua.ControlPlane;

/// <summary>
/// Recycles a published Lambda alias without substituting a different image.
/// Lambda has no restart API. The executor republishes the alias's current image
/// and moves the alias only after the published image still matches.
/// </summary>
internal interface ILambdaAliasRecycle
{
    /// <summary>Returns the image digest that was republished. Does not move the alias when the image would change.</summary>
    Task<string> RecycleAsync(
        string functionName,
        string aliasName,
        string? region,
        string? serviceUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>Request bodies for a Lambda alias recycle. Code updates carry the current image and nothing else.</summary>
internal static class LambdaAliasRecycleRequests
{
    public static UpdateFunctionCodeRequest UpdateCode(string functionName, string imageUri)
        => new()
        {
            FunctionName = functionName,
            ImageUri = imageUri
        };

    public static PublishVersionRequest Publish(string functionName)
        => new() { FunctionName = functionName };

    public static UpdateAliasRequest MoveAlias(string functionName, string aliasName, string functionVersion)
        => new()
        {
            FunctionName = functionName,
            Name = aliasName,
            FunctionVersion = functionVersion
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
        var before = await client.GetFunctionAsync(
                new GetFunctionRequest { FunctionName = functionName, Qualifier = aliasName },
                cancellationToken)
            .ConfigureAwait(false);
        var image = LambdaImageGuard.RequireImmutable(ImageOf(before));

        var alias = await client.GetAliasAsync(
                new GetAliasRequest { FunctionName = functionName, Name = aliasName },
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(alias.FunctionVersion) ||
            !long.TryParse(alias.FunctionVersion, out var currentVersion) ||
            currentVersion <= 0)
        {
            throw new InvalidOperationException(
                "Lambda restart requires a published numeric alias version. The alias was not moved.");
        }

        var updated = await client.UpdateFunctionCodeAsync(
                LambdaAliasRecycleRequests.UpdateCode(functionName, image),
                cancellationToken)
            .ConfigureAwait(false);
        if (!await WaitUntilSuccessfulAsync(client, functionName, updated.LastUpdateStatus, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Lambda restart did not publish the current image. The alias was not moved.");
        }

        var published = await client.PublishVersionAsync(
                LambdaAliasRecycleRequests.Publish(functionName),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(published.Version) ||
            !long.TryParse(published.Version, out var publishedVersion) ||
            publishedVersion <= 0)
        {
            throw new InvalidOperationException(
                "Lambda restart did not return a numeric version. The alias was not moved.");
        }

        var publishedFunction = await client.GetFunctionAsync(
                new GetFunctionRequest { FunctionName = functionName, Qualifier = published.Version },
                cancellationToken)
            .ConfigureAwait(false);
        LambdaImageGuard.RequireUnchanged(image, ImageOf(publishedFunction));

        await client.UpdateAliasAsync(
                LambdaAliasRecycleRequests.MoveAlias(functionName, aliasName, published.Version),
                cancellationToken)
            .ConfigureAwait(false);

        var after = await client.GetFunctionAsync(
                new GetFunctionRequest { FunctionName = functionName, Qualifier = aliasName },
                cancellationToken)
            .ConfigureAwait(false);
        return LambdaImageGuard.RequireUnchanged(image, ImageOf(after));
    }

    private static string? ImageOf(GetFunctionResponse response)
        => string.IsNullOrWhiteSpace(response.Code?.ResolvedImageUri)
            ? response.Code?.ImageUri
            : response.Code.ResolvedImageUri;

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
