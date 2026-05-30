// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Import;
using Honua.Server.Features.Migration;
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
