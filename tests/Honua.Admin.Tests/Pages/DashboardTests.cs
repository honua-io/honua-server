// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin.Pages;
using MudBlazor.Services;

namespace Honua.Admin.Tests.Pages;

public sealed class DashboardTests
{
    [Fact]
    public void Dashboard_RendersPrimaryCards()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();

        var cut = ctx.RenderComponent<Home>();

        Assert.Contains("Connections", cut.Markup);
        Assert.Contains("Layers", cut.Markup);
        Assert.Contains("Health", cut.Markup);
    }
}
