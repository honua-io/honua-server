// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using System.Net.Http;
using Xunit;

namespace Honua.Server.Tests.Import;

public sealed class ImportHttpClientHelperTests
{
    [Fact]
    public void CreatePinnedDnsHttpMessageHandler_UsesSocketsHandlerWithPinnedConnectCallback()
    {
        var handler = ImportHttpClientHelper.CreatePinnedDnsHttpMessageHandler();

        var socketsHandler = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        socketsHandler.AllowAutoRedirect.Should().BeFalse();
        socketsHandler.ConnectCallback.Should().NotBeNull();
    }
}
