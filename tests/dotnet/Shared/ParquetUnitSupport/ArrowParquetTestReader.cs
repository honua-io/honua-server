// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using ParquetSharp.Arrow;

namespace Honua.Tests.ParquetUnitSupport;

// Opens test payloads with the reader settings the import path uses: no pre-buffering, no reader threads.
// With pre-buffering on, an Arrow IO thread can release the last native reference to the managed stream
// after Dispose; the native destructor then calls Close through a collected delegate and the test host
// aborts with an AccessViolationException.
internal static class ArrowParquetTestReader
{
    public static FileReader Open(Stream stream)
    {
        using var properties = CreateProperties();
        return new FileReader(stream, arrowProperties: properties);
    }

    public static FileReader Open(byte[] payload)
    {
        using var properties = CreateProperties();
        return new FileReader(new MemoryStream(payload), arrowProperties: properties);
    }

    public static FileReader OpenLeavingStreamOpen(Stream stream)
    {
        using var properties = CreateProperties();
        return new FileReader(stream, arrowProperties: properties, leaveOpen: true);
    }

    private static ArrowReaderProperties CreateProperties()
    {
        var properties = ArrowReaderProperties.GetDefault();
        try
        {
            properties.PreBuffer = false;
            properties.UseThreads = false;
            return properties;
        }
        catch
        {
            properties.Dispose();
            throw;
        }
    }
}
