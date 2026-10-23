// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.FileStorage;

/// <summary>
/// Regression for #4998: against an S3-compatible endpoint (FileStorage:AwsS3:ServiceUrl) the
/// range reader must sign with the configured region instead of resolving one from the ambient
/// chain, whose last step (EC2 instance metadata) never answers inside a container. Every call is
/// bounded, so a regression fails fast instead of hanging the run.
/// </summary>
public sealed class AwsS3RangeReaderEndpointTests
{
    // Deliberately not a common default (us-east-1 / us-west-2), so an ambient AWS_REGION or
    // shared profile on the test host cannot mask a client that ignores the configured region.
    private const string Region = "eu-west-3";
    private const string Bucket = "roster-fixtures";
    private const string Key = "cog/roster-3857.tif";
    private const string ETag = "\"4998-range-probe\"";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task RangeReader_WithServiceUrl_HeadThenRangedGetCompleteSignedForConfiguredRegion()
    {
        var content = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        using var endpoint = FakeS3Endpoint.Start(content, ETag);
        using var provider = BuildProvider(endpoint.ServiceUrl);
        var reader = provider.GetServices<ICloudRangeReader>()
            .Single(candidate => candidate.Provider == CloudStorageProvider.AwsS3);
        using var cancellation = new CancellationTokenSource(Budget);

        var metadata = await reader.GetObjectMetadataAsync(Bucket, Key, cancellation.Token)
            .WaitAsync(Budget);
        var bytes = await reader.ReadRangeAsync(Bucket, Key, 8, 16, metadata.ETag!, cancellation.Token)
            .WaitAsync(Budget);

        metadata.SizeBytes.Should().Be(content.Length);
        bytes.Should().Equal(content.Skip(8).Take(16));
        var requests = endpoint.Requests.ToArray();
        requests.Select(request => request.Method).Should().Equal("HEAD", "GET");
        requests[1].Headers["range"].Should().Be("bytes=8-23");
        requests[1].Headers["if-match"].Should().Be(ETag);
        requests.Should().OnlyContain(
            request => request.Headers["authorization"].Contains($"/{Region}/s3/aws4_request", StringComparison.Ordinal),
            "the SigV4 scope must come from FileStorage:AwsS3:Region, not the ambient region chain");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void CreateClient_WithServiceUrl_PinsConfiguredRegionForSigning()
    {
        using var client = AwsS3FileStorage.CreateClient(new AwsS3Options
        {
            Region = Region,
            ServiceUrl = "http://localstack:4566",
            ForcePathStyle = true,
            AccessKeyId = "test",
            SecretAccessKey = "test",
        });

        client.Config.ServiceURL.Should().Be("http://localstack:4566/");
        client.Config.AuthenticationRegion.Should().Be(Region);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void CreateClient_WithoutServiceUrl_UsesRegionalEndpoint()
    {
        using var client = AwsS3FileStorage.CreateClient(new AwsS3Options { Region = Region });

        client.Config.RegionEndpoint!.SystemName.Should().Be(Region);
        string.IsNullOrEmpty(client.Config.ServiceURL).Should().BeTrue();
    }

    private static ServiceProvider BuildProvider(string serviceUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Provider"] = "Local",
                ["FileStorage:AwsS3:BucketName"] = Bucket,
                ["FileStorage:AwsS3:Region"] = Region,
                ["FileStorage:AwsS3:ServiceUrl"] = serviceUrl,
                ["FileStorage:AwsS3:ForcePathStyle"] = "true",
                ["FileStorage:AwsS3:AccessKeyId"] = "test",
                ["FileStorage:AwsS3:SecretAccessKey"] = "test",
            })
            .Build();
        var services = new ServiceCollection();
        services.Configure<CloudStorageOptions>(configuration.GetSection("FileStorage"));
        services.AddAwsCloudRangeReader(configuration);
        return services.BuildServiceProvider();
    }

    private sealed record ObservedRequest(string Method, IReadOnlyDictionary<string, string> Headers);

    /// <summary>Minimal loopback S3 object endpoint answering HEAD and single-range GET.</summary>
    private sealed class FakeS3Endpoint : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _content;
        private readonly string _etag;
        private readonly CancellationTokenSource _stop = new();

        private FakeS3Endpoint(byte[] content, string etag)
        {
            _content = content;
            _etag = etag;
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        public ConcurrentQueue<ObservedRequest> Requests { get; } = new();

        public string ServiceUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public static FakeS3Endpoint Start(byte[] content, string etag)
        {
            var endpoint = new FakeS3Endpoint(content, etag);
            endpoint._listener.Start();
            _ = endpoint.AcceptLoopAsync();
            return endpoint;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Listener stopped.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } requestLine)
                    {
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                        {
                            var colon = line.IndexOf(':', StringComparison.Ordinal);
                            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                        }

                        var method = requestLine.Split(' ')[0];
                        Requests.Enqueue(new ObservedRequest(method, headers));
                        await stream.WriteAsync(BuildResponse(method, headers), _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // Client closed or endpoint stopped.
                }
            }
        }

        private byte[] BuildResponse(string method, Dictionary<string, string> headers)
        {
            var head = new StringBuilder();
            byte[] body = [];
            if (method == "GET" && headers.TryGetValue("Range", out var range))
            {
                var bounds = range["bytes=".Length..].Split('-');
                var start = int.Parse(bounds[0], CultureInfo.InvariantCulture);
                var end = int.Parse(bounds[1], CultureInfo.InvariantCulture);
                body = _content[start..(end + 1)];
                head.Append("HTTP/1.1 206 Partial Content\r\n");
                head.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{end}/{_content.Length}\r\n");
                head.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
            }
            else
            {
                head.Append("HTTP/1.1 200 OK\r\n");
                head.Append(CultureInfo.InvariantCulture, $"Content-Length: {_content.Length}\r\n");
            }

            head.Append(CultureInfo.InvariantCulture, $"ETag: {_etag}\r\n");
            head.Append("Content-Type: image/tiff\r\n\r\n");
            return [.. Encoding.ASCII.GetBytes(head.ToString()), .. method == "HEAD" ? [] : body];
        }
    }
}
