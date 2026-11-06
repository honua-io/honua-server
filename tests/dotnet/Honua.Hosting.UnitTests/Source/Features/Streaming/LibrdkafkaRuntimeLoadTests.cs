// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Confluent.Kafka;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;
using FluentAssertions;
using Xunit;

namespace Honua.Server.Tests.Features.Streaming;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class LibrdkafkaRuntimeLoadTests
{
    [LinuxFact]
    public void Producer_LoadsTheGlibcBuildThisOperatingSystemCanLink()
    {
        var gssapiBuildCanLink = SharedLibraryLoads("libsasl2.so.3");

        using var producer = new ProducerBuilder<byte[], byte[]>(new ProducerConfig
        {
            BootstrapServers = "127.0.0.1:1",
            SocketTimeoutMs = 10,
            MessageTimeoutMs = 10,
        }).Build();

        Library.VersionString.Should().NotBeNullOrWhiteSpace();

        var loaded = LoadedLibraryNames();
        loaded.Should().NotContain("alpine-librdkafka.so");

        if (RuntimeInformation.ProcessArchitecture == CpuArchitecture.X64 && !gssapiBuildCanLink)
        {
            loaded.Should().Contain("centos8-librdkafka.so");
            loaded.Should().NotContain("librdkafka.so");
            return;
        }

        loaded.Should().Contain("librdkafka.so");
        loaded.Should().NotContain("centos8-librdkafka.so");
    }

    private static bool SharedLibraryLoads(string libraryName)
    {
        if (!NativeLibrary.TryLoad(libraryName, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }

    private static string[] LoadedLibraryNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            var path = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (path is null || !path.Contains("librdkafka", StringComparison.Ordinal))
            {
                continue;
            }

            names.Add(Path.GetFileName(path));
        }

        return [.. names];
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "librdkafka is loaded from the Linux process map.";
        }
    }
}
