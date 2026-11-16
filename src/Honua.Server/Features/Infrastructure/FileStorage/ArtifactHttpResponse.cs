// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Models;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Infrastructure.FileStorage;

// Call only after checking the artifact's current source and authorization policy.
internal static class ArtifactHttpResponse
{
    internal static async Task<IResult> ServeAsync(
        HttpContext context, CloudFile metadata, ArtifactByteReader rangeProxy, CancellationToken cancellationToken)
    {
        var response = context.Response;
        var conditions = ArtifactHttpConditions.Evaluate(context, metadata);
        if (conditions.StatusCode is { } statusCode)
        {
            return TypedResults.StatusCode(statusCode);
        }
        response.ContentType = metadata.ContentType;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            response.ContentLength = metadata.SizeBytes;
            return TypedResults.Empty;
        }

        var rangeHeader = conditions.RangeHeader;
        var rangeResult = await rangeProxy.ReadRangeAsync(metadata, rangeHeader, cancellationToken).ConfigureAwait(false);

        switch (rangeResult.Outcome)
        {
            case ArtifactRangeOutcome.RevisionChanged:
            case ArtifactRangeOutcome.Unsupported:
                return ArtifactHttpConditions.ReadFailure(context, rangeResult.Outcome);

            case ArtifactRangeOutcome.Unsatisfiable:
                response.Headers[HeaderNames.ContentRange] = $"bytes */{rangeResult.TotalSize}";
                return TypedResults.StatusCode(StatusCodes.Status416RangeNotSatisfiable);

            case ArtifactRangeOutcome.NotFound:
                return ArtifactHttpConditions.ReadFailure(context, rangeResult.Outcome);

            case ArtifactRangeOutcome.TooLarge:
                return StandardErrorHelpers.CreatePayloadTooLarge(
                    context,
                    $"The artifact is {rangeResult.TotalSize:N0} bytes, which exceeds the " +
                    $"{ArtifactByteReader.MaxDirectFullStreamBytes:N0}-byte limit for one proxy response. " +
                    "Request a smaller byte range (this endpoint advertises Accept-Ranges: bytes).");

            case ArtifactRangeOutcome.Partial:
                response.Headers[HeaderNames.ContentRange] = $"bytes {rangeResult.Start}-{rangeResult.End}/{rangeResult.TotalSize}";
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.ContentLength = rangeResult.Payload?.LongLength ?? 0L;
                if (rangeResult.Payload is { Length: > 0 })
                {
                    await response.Body.WriteAsync(rangeResult.Payload, cancellationToken).ConfigureAwait(false);
                }
                return TypedResults.Empty;

            case ArtifactRangeOutcome.Full:
            default:
                var fullResult = await rangeProxy.ReadFullAsync(metadata, cancellationToken).ConfigureAwait(false);
                if (fullResult.Outcome != ArtifactRangeOutcome.Full)
                {
                    return ArtifactHttpConditions.ReadFailure(context, fullResult.Outcome);
                }
                return Results.Stream(fullResult.Stream!, response.ContentType, enableRangeProcessing: false);
        }
    }
}
