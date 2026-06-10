// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Reads HTTP response bodies from migration sources with an explicit byte ceiling.
/// Migration scans and discovery run against arbitrary operator-supplied services, so
/// response sizes are source-controlled; unbounded <c>GetStringAsync</c>/<c>ReadAsStringAsync</c>
/// buffering would let a single multi-GB capabilities/describe document drive the process
/// toward OOM.
/// </summary>
internal static class MigrationHttpContentReader
{
    /// <summary>
    /// Default ceiling for buffered migration documents (capabilities, DescribeFeatureType,
    /// REST/SLD payloads). Real-world documents are at most a few tens of MB.
    /// </summary>
    public const long DefaultMaxResponseBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Reads the response content as a string, failing with <see cref="HttpRequestException"/>
    /// when the declared or actual body size exceeds <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<string> ReadStringWithLimitAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new HttpRequestException(
                $"Migration source response declares {declared} bytes, exceeding the {maxBytes}-byte limit.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffered = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffered.Length + read > maxBytes)
                    {
                        throw new HttpRequestException(
                            $"Migration source response exceeded the {maxBytes}-byte limit.");
                    }

                    buffered.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return DecodeString(buffered, response.Content.Headers.ContentType?.CharSet);
        }
    }

    private static string DecodeString(MemoryStream buffered, string? charSet)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charSet))
        {
            try
            {
                encoding = Encoding.GetEncoding(charSet.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Unknown charset advertised by the source; fall back to UTF-8.
            }
        }

        var text = encoding.GetString(buffered.GetBuffer(), 0, (int)buffered.Length);

        // Strip a byte-order mark so XML/JSON parsers that reject U+FEFF keep working,
        // matching HttpContent.ReadAsStringAsync semantics.
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
