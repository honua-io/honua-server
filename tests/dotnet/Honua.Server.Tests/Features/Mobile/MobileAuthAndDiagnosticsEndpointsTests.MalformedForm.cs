// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Mobile;

public sealed partial class MobileAuthAndDiagnosticsEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /oauth/token")]
    public async Task TokenRefresh_MultipartWithoutBoundary_ReturnsBadRequest()
    {
        using var content = new StringContent("refresh_token=refresh-token", Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");

        using var response = await _anonymousClient.PostAsync("/oauth/token", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /oauth/token")]
    public async Task TokenRefresh_TruncatedMultipartBody_ReturnsBadRequest()
    {
        const string truncated = "--b9486\r\nContent-Disposition: form-data; name=\"refresh_token\"\r\n\r\nrefresh-tok";
        using var content = new StringContent(truncated, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=b9486");

        using var response = await _anonymousClient.PostAsync("/oauth/token", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
