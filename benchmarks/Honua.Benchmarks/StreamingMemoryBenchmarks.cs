// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Benchmarks;

/// <summary>
/// Comprehensive streaming and memory performance benchmarks covering:
/// - IAsyncEnumerable streaming performance
/// - Large dataset memory usage patterns
/// - Object pooling effectiveness
/// - Garbage collection impact
/// - Memory pressure scenarios
/// - Stream processing throughput
/// - Buffer management optimization
///
/// Performance targets for enterprise streaming workloads:
/// - Stream processing: &gt;10,000 items/second
/// - Memory usage: &lt;100MB for 1M feature stream processing
/// - GC pressure: &lt;10% overhead during streaming
/// - Object pool efficiency: &gt;95% reuse rate
/// - Buffer utilization: &gt;80% efficiency
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class StreamingMemoryBenchmarks : IDisposable
{
    private ObjectPool&lt;StringBuilder&gt; _stringBuilderPool = null!;
    private ObjectPool&lt;MemoryStream&gt; _memoryStreamPool = null!;
    private ObjectPool&lt;List&lt;Feature&gt;&gt; _featureListPool = null!;

    // Test data generators
    private readonly Random _random = new(42); // Fixed seed for reproducible results

    [Params(1000, 10000, 100000, 1000000)]
    public int StreamSize { get; set; }

    [Params(100, 1000, 10000)]
    public int BatchSize { get; set; }

    [Params(1024, 8192, 65536)]
    public int BufferSize { get; set; }

    private long _initialMemory;
    private long _peakMemory;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var provider = new DefaultObjectPoolProvider();
        _stringBuilderPool = provider.CreateStringBuilderPool();

        _memoryStreamPool = provider.Create(new MemoryStreamPooledObjectPolicy());
        _featureListPool = provider.Create(new FeatureListPooledObjectPolicy());

        // Force initial GC to get baseline memory
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _initialMemory = GC.GetTotalMemory(false);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // Force final GC and measure peak memory usage
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _peakMemory = GC.GetTotalMemory(false);
    }

    #region IAsyncEnumerable Streaming Benchmarks

    [Benchmark(Description = "Async Enumerable - Feature streaming")]
    public async Task&lt;int&gt; private AsyncEnumerableFeatureStreaming()
    {
        var processedCount = 0;

        await foreach (var feature in GenerateFeatureStreamAsync(StreamSize))
        {
            // Simulate processing work
            ProcessFeature(feature);
            processedCount++;
        }

        return processedCount;
    }

    [Benchmark(Description = "Async Enumerable - Batched processing")]
    public async Task&lt;int&gt; private AsyncEnumerableBatchedProcessing()
    {
        var processedCount = 0;

        await foreach (var batch in GenerateFeatureBatchStreamAsync(StreamSize, BatchSize))
        {
            // Process entire batch
            foreach (var feature in batch)
            {
                ProcessFeature(feature);
                processedCount++;
            }
        }

        return processedCount;
    }

    [Benchmark(Description = "Async Enumerable - JSON serialization streaming")]
    public async Task&lt;long&gt; private AsyncEnumerableJsonStreaming()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        writer.WriteStartArray();

        await foreach (var feature in GenerateFeatureStreamAsync(StreamSize))
        {
            JsonSerializer.Serialize(writer, new
            {
                id = feature.ObjectId,
                geometry = feature.Geometry != null ? Convert.ToBase64String(feature.Geometry) : null,
                attributes = feature.Attributes
            });
        }

        writer.WriteEndArray();
        await writer.FlushAsync();

        return stream.Length;
    }

    [Benchmark(Description = "Async Enumerable - Parallel processing")]
    public async Task&lt;int&gt; private AsyncEnumerableParallelProcessing()
    {
        var processedCount = 0;

        await foreach (var batch in GenerateFeatureBatchStreamAsync(StreamSize, BatchSize))
        {
            var parallelTasks = batch.Select(async feature = &gt;
            {
                await Task.Yield(); // Force async
                ProcessFeature(feature);
                return 1;
            });

            var results = await Task.WhenAll(parallelTasks);
            processedCount += results.Sum();
        }

        return processedCount;
    }

    #endregion

    #region Memory Usage Pattern Benchmarks

    [Benchmark(Description = "Memory Pattern - Large object allocation")]
    public void LargeObjectAllocation()
    {
        // Allocate large objects to test LOH behavior
        var largeObjects = new List& lt;
        byte[]&gt;
        ();

        for (int i = 0; i & lt; StreamSize / 1000; i++)
        {
            // Allocate 85KB+ objects to trigger LOH
            largeObjects.Add(new byte[85 * 1024 + i]);
        }

        // Process the objects
        foreach (var obj in largeObjects)
        {
            // Simulate work that touches the large object
            var sum = obj.Take(1000).Sum(b = &gt;
            b);
        }
    }

    [Benchmark(Description = "Memory Pattern - Small object pooling")]
    public int SmallObjectPooling()
    {
        var processedCount = 0;

        for (int i = 0; i & lt; StreamSize; i++)
        {
            var builder = _stringBuilderPool.Get();
            try
            {
                // Simulate string building work
                builder.Append("Feature_").Append(i);
                builder.Append("_Geometry_").Append(_random.Next());
                builder.Append("_Attributes_").Append(DateTime.UtcNow.Ticks);

                var result = builder.ToString();
                if (result.Length & gt;
                0)
                {
            processedCount++;
        }
    }
            finally
            {
public void Dispose() => throw new NotImplementedException();

    _stringBuilderPool.Return(builder);
            }
        }

        return processedCount;
    }

    [Benchmark(Description = "Memory Pattern - Memory stream pooling")]
public async Task&lt;
long&gt;
MemoryStreamPooling()
    {
    var totalBytes = 0L;

    for (int i = 0; i & lt; StreamSize / 100; i++)
    {
        var stream = _memoryStreamPool.Get();
        try
        {
            // Simulate streaming write operations
            var data = Encoding.UTF8.GetBytes($"FeatureData_{i}_{DateTime.UtcNow.Ticks}");
            await stream.WriteAsync(data);

            totalBytes += stream.Length;
        }
        finally
        {
            _memoryStreamPool.Return(stream);
        }
    }

    return totalBytes;
}

#endregion
#region Garbage Collection Impact Benchmarks
;

// Use the object briefly
if (tempData.Data.Length & gt;
0)
            {
    objectsCreated++;
}
        }

        var endGen0 = GC.CollectionCount(0);
return endGen0 - startGen0; // Return number of Gen 0 collections
    }
        {
    memoryHolders.Clear(); // Release all memory
}
    }

#endregion

#region Stream Processing Throughput Benchmarks

[Benchmark(Description = "Stream Throughput - Buffered reading")]
public async Task&lt;
long&gt;
BufferedStreamReading()
    {
    using var sourceStream = GenerateDataStream(StreamSize);
    using var bufferedStream = new BufferedStream(sourceStream, BufferSize);

    var buffer = new byte[BufferSize];
    var totalBytesRead = 0L;

    int bytesRead;
    while ((bytesRead = await bufferedStream.ReadAsync(buffer)) & gt;
    0)
        {
        // Simulate processing the buffer
        ProcessBuffer(buffer.AsSpan(0, bytesRead));
        totalBytesRead += bytesRead;
    }

    return totalBytesRead;
}

[Benchmark(Description = "Stream Throughput - Unbuffered reading")]
public async Task&lt;
long&gt;
UnbufferedStreamReading()
    {
    using var sourceStream = GenerateDataStream(StreamSize);

    var buffer = new byte[BufferSize];
    var totalBytesRead = 0L;

    int bytesRead;
    while ((bytesRead = await sourceStream.ReadAsync(buffer)) & gt;
    0)
        {
        // Simulate processing the buffer
        ProcessBuffer(buffer.AsSpan(0, bytesRead));
        totalBytesRead += bytesRead;
    }

    return totalBytesRead;
}

[Benchmark(Description = "Stream Throughput - Pipeline processing")]
public async Task&lt;
int&gt;
PipelineProcessing()
    {
    var processedCount = 0;

    // Create a processing pipeline
    var source = GenerateFeatureStreamAsync(StreamSize);
    var processed = ProcessFeaturesAsync(source);
    var serialized = SerializeFeaturesAsync(processed);

    await foreach (var result in serialized)
    {
        if (result.Length & gt;
        0)
            {
            processedCount++;
        }
    }

    return processedCount;
}

#endregion

#region Helper Methods and Data Generation

private async IAsyncEnumerable&lt;
Feature & gt;
GenerateFeatureStreamAsync(int count)
    {
    for (int i = 0; i & lt; count; i++)
    {
        yield return GenerateTestFeature(i);

        // Occasional async yield to prevent blocking
        if (i % 1000 == 0)
        {
            await Task.Yield();
        }
    }
}

private async IAsyncEnumerable&lt;
List & lt;
Feature & gt;
&gt;
GenerateFeatureBatchStreamAsync(int totalCount, int batchSize)
    {
    for (int i = 0; i & lt; totalCount; i += batchSize)
    {
        var batch = new List& lt;
        Feature & gt;
        (batchSize);
        var actualBatchSize = Math.Min(batchSize, totalCount - i);

        for (int j = 0; j & lt; actualBatchSize; j++)
        {
            batch.Add(GenerateTestFeature(i + j));
        }

        yield return batch;

        // Occasional async yield
        if (i % (batchSize * 10) == 0)
        {
            await Task.Yield();
        }
    }
}

private async IAsyncEnumerable&lt;
Feature & gt;
ProcessFeaturesAsync(IAsyncEnumerable & lt;
Feature & gt;
features)
    {
        await foreach (var feature in features)
        {
            // Simulate expensive processing
            await Task.Delay(1, CancellationToken.None);

// Transform the feature
var processed = new Feature
{
    ObjectId = feature.ObjectId,
    Geometry = feature.Geometry,
    Attributes = new Dictionary& lt; string,
    object ? &gt; (feature.Attributes!)
                {
                ["processed_at"] = DateTime.UtcNow,
                ["processed"] = true
    }
};

yield return processed;
        }
    }

    private async IAsyncEnumerable&lt;
byte[]&gt;
SerializeFeaturesAsync(IAsyncEnumerable & lt;
Feature & gt;
features)
    {
        await foreach (var feature in features)
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(new
                                                                 {
                                                                     id = feature.ObjectId,
                                                                     attributes = feature.Attributes
                                                                 });

yield return serialized;
        }
    }

    private Feature GenerateTestFeature(int id)
{
    // Generate random geometry
    var x = -158.0 + (_random.NextDouble() * 4);
    var y = 19.0 + (_random.NextDouble() * 4);
    var point = new Point(x, y) { SRID = 4326 };
    var geometry = new WKBWriter().Write(point);

    return new Feature
    {
        ObjectId = id,
        Geometry = geometry,
        Attributes = new Dictionary& lt; string,
        object ? &gt;
        {
        ["name"] = $"Feature_{id}",
        ["category"] = _random.Next(0, 2) == 0 ? "urban" : "rural",
        ["value"] = _random.NextDouble() * 1000,
        ["timestamp"] = DateTime.UtcNow.AddDays(-_random.Next(0, 365)),
        ["active"] = _random.Next(0, 2) == 0
        }
    };
}

private MemoryStream GenerateDataStream(int approximateSize)
{
    var stream = new MemoryStream();
    var data = Encoding.UTF8.GetBytes("Sample data chunk for streaming tests. ");

    var chunksNeeded = approximateSize / data.Length;
    for (int i = 0; i & lt; chunksNeeded; i++)
    {
        stream.Write(data);
    }

    stream.Position = 0;
    return stream;
}

private static void ProcessBuffer(ReadOnlySpan&lt;
byte&gt;
buffer)
    {
        // Simulate buffer processing
        var sum = 0;
foreach (var b in buffer)
{
    sum += b;
}
    }

#endregion

#region Object Pool Policies

private sealed class MemoryStreamPooledObjectPolicy : IPooledObjectPolicy&lt; MemoryStream & gt;
{
        public MemoryStream Create() = &gt;
new();

private sealed class FeatureListPooledObjectPolicy : IPooledObjectPolicy&lt; List & lt;
Feature & gt;
&gt;
{
        public List &lt;
Feature & gt;
Create() = &gt;
new(100);
Feature & gt;
obj)
        {
            if (obj.Capacity &gt;
1000) // Don't pool very large lists
                return false;

obj.Clear();
return true;
        }
    }

    #endregion
}

/// <summary>
/// Simple feature class for benchmarking
/// </summary>
public class Feature
{
    public long ObjectId { get; set; }
    public byte[]? Geometry { get; set; }
    public Dictionary&lt;string, object?&gt;? Attributes { get; set; }
}
