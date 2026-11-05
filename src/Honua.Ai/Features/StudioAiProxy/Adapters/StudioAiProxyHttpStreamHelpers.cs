// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;

namespace Honua.Ai.StudioAiProxy.Adapters;

// The try/catch lives in an ordinary method because C# forbids yield return inside try/catch.
internal static class StudioAiProxyHttpStreamHelpers
{
    internal const int MaximumErrorBodyBytes = 4096;

    // Caller cancellation is rethrown and a provider timeout is returned as timed-out, so the two are not the same failure.
    public static async Task<(HttpResponseMessage? Response, bool TimedOut, HttpRequestException? RequestError)> SendSafeAsync(
        HttpClient client,
        HttpRequestMessage request,
        IAiExecutionPermit permit,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            await permit.AuthorizeSendAsync(linkedToken).ConfigureAwait(false);
            var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken)
                .ConfigureAwait(false);
            return (response, false, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, true, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, false, ex);
        }
    }

    public static async Task<(string? Line, bool TimedOut, IOException? IoError)> ReadLineSafeAsync(
        StreamReader reader,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            var line = await reader.ReadLineAsync(linkedToken).ConfigureAwait(false);
            return (line, false, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, true, null);
        }
        catch (IOException ex)
        {
            return (null, false, ex);
        }
    }

    public static async Task<(Stream? Stream, bool TimedOut, Exception? Failure)> OpenStreamSafeAsync(
        HttpContent content,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            return (await content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false), false, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, false, ex);
        }
    }

    public static async Task<(string Body, bool TimedOut, Exception? Failure, int BytesBuffered)> ReadErrorBodySafeAsync(
        HttpContent content,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        var (stream, timedOut, failure) = await OpenStreamSafeAsync(content, linkedToken, callerToken).ConfigureAwait(false);
        if (timedOut || failure is not null)
        {
            return (string.Empty, timedOut, failure, 0);
        }

        var responseStream = stream!;
        await using (responseStream)
        {
            var buffer = new byte[MaximumErrorBodyBytes];
            var total = 0;
            try
            {
                while (total < buffer.Length)
                {
                    var read = await responseStream.ReadAsync(buffer.AsMemory(total), linkedToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                return (System.Text.Encoding.UTF8.GetString(buffer, 0, total), false, null, total);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return (string.Empty, true, null, total);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return (string.Empty, false, ex, total);
            }
        }
    }
}
