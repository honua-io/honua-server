import SpatialReference from "@arcgis/core/geometry/SpatialReference";
import Extent from "@arcgis/core/geometry/Extent";
import Polyline from "@arcgis/core/geometry/Polyline";
import Polygon from "@arcgis/core/geometry/Polygon";

const sr = new SpatialReference({ wkid: 4326 });

const extent = new Extent({
  xmin: -10,
  ymin: -5,
  xmax: 30,
  ymax: 15,
  spatialReference: sr,
});

const polyline = new Polyline({
  paths: [
    [
      [0, 0],
      [1, 1],
    ],
  ],
  spatialReference: sr,
});

const polygon = new Polygon({
  rings: [
    [
      [0, 0],
      [10, 0],
      [10, 10],
      [0, 0],
    ],
  ],
  spatialReference: sr,
});

void extent;
void polyline;
void polygon;
