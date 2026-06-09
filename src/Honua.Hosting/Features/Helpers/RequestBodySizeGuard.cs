// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using Honua.Core.Configuration;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Helpers;

internal static class RequestBodySizeGuard
{
    private const int BufferSize = 81920;
    private const string PayloadTooLargeMessage = "Request payload is too large.";

    public readonly record struct TextReadResult(
        bool TooLarge,
        string? Body,
        IResult? ErrorResult);

    public static long ResolveMaxBodyBytes(IOptions<LimitsOptions> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return limits.Value.MaxUploadSizeBytes > 0
            ? limits.Value.MaxUploadSizeBytes
            : 100 * 1024 * 1024;
    }

    public static long ResolveMaxBodyBytes(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ResolveMaxBodyBytes(context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>());
    }

    public static IResult? RejectIfDeclaredTooLarge(HttpContext context, long maxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.ContentLength > maxBodyBytes
            ? StandardErrorHelpers.CreatePayloadTooLarge(context, PayloadTooLargeMessage)
            : null;
    }

    public static async Task<TextReadResult> ReadUtf8TextAsync(
        HttpContext context,
        long maxBodyBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var declaredTooLarge = RejectIfDeclaredTooLarge(context, maxBodyBytes);
        if (declaredTooLarge != null)
        {
            return new TextReadResult(true, null, declaredTooLarge);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var output = new MemoryStream();
            while (true)
            {
                var read = await context.Request.Body
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maxBodyBytes)
                {
                    return new TextReadResult(
                        true,
                        null,
                        StandardErrorHelpers.CreatePayloadTooLarge(context, PayloadTooLargeMessage));
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return new TextReadResult(false, Encoding.UTF8.GetString(output.ToArray()), null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class RequestBodyTooLargeException : Exception
{
    public RequestBodyTooLargeException()
        : base("Request payload is too large.")
    {
    }
}
