// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.CogParser;

// The requested tile's position inside its ancestor at native zoom: Shift zoom levels finer,
// Column and Row counted in requested-tile units from the ancestor's top-left corner.
public readonly record struct CogOverzoomQuadrant(int Shift, int Column, int Row);
