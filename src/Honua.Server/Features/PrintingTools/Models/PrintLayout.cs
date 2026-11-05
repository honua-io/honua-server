// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.PrintingTools.Models;

internal sealed class PrintLayoutTemplate
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required float PageWidth { get; init; }

    public required float PageHeight { get; init; }

    public required LayoutSlot MapFrame { get; init; }

    public LayoutSlot? Title { get; init; }

    public LayoutSlot? Legend { get; init; }

    public LayoutSlot? ScaleBar { get; init; }

    public LayoutSlot? NorthArrow { get; init; }

    public LayoutSlot? Attribution { get; init; }

    public bool IsMapOnly => Title is null && Legend is null && ScaleBar is null && NorthArrow is null;
}

internal sealed class LayoutSlot
{
    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }
}
