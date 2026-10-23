// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// The honua-sdk-js canonical <c>honua_map_package.v1</c> artifact, copied verbatim from
/// <c>examples/runtime-parity-showcase/fixtures/map-package.json</c> at honua-sdk-js
/// <c>cd96ff4</c>. It validates against the SDK's published
/// <c>schemas/honua-map-package.v1.json</c>, and the sdk-js#1426 replay saw the 2026.1
/// candidate refuse it as a map draft body (honua-server#4898). Do not edit it to make a
/// server test pass: re-copy it from the SDK instead.
/// </summary>
internal static class MapPackageCanonicalFixture
{
    public const string RuntimeParityShowcase = """
        {
          "mapPackageId": "runtime-parity-showcase",
          "format": "honua_map_package.v1",
          "status": "Ready",
          "createdAt": "2026-05-01T12:00:00.000Z",
          "updatedAt": "2026-05-01T12:00:00.000Z",
          "themeId": "runtime-parity-theme",
          "theme": {
            "themeId": "runtime-parity-theme",
            "tokens": {
              "incidentStroke": "#ffffff",
              "zoneOutline": "#31505f"
            }
          },
          "sourceBindings": [
            {
              "sourceId": "basemap-grid",
              "protocol": "raster_tile",
              "locator": {
                "url": "/__runtime-parity-showcase__/tiles/{z}/{x}/{y}.png"
              },
              "attribution": "Fixture raster tile lane"
            }
          ],
          "styleRefs": [
            {
              "styleId": "runtime-parity-status-ramp",
              "label": "Runtime status ramp",
              "body": {
                "incident-points": {
                  "paint": {
                    "circle-stroke-color": "{theme:incidentStroke}",
                    "circle-stroke-width": 2
                  }
                },
                "zone-outline": {
                  "paint": {
                    "line-color": "{theme:zoneOutline}"
                  }
                }
              }
            }
          ],
          "initialView": {
            "center": [
              -157.865,
              21.302
            ],
            "zoom": 11.4,
            "pitch": 0,
            "bearing": 0,
            "crs": "EPSG:4326"
          },
          "legend": [
            {
              "label": "Open",
              "color": "#d93f3f"
            },
            {
              "label": "Monitoring",
              "color": "#d9902f"
            },
            {
              "label": "Resolved",
              "color": "#2f8f75"
            },
            {
              "label": "Operations zone",
              "color": "#72a7b8"
            }
          ],
          "popupBindings": [
            {
              "sourceId": "ops-incidents",
              "fieldName": "name",
              "title": "Incident"
            }
          ],
          "mapSpec": {
            "version": 8,
            "sources": {
              "basemap-grid": {
                "type": "raster",
                "tiles": [
                  "/__runtime-parity-showcase__/tiles/{z}/{x}/{y}.png"
                ],
                "tileSize": 256
              },
              "ops-zones": {
                "type": "geojson",
                "data": {
                  "type": "FeatureCollection",
                  "features": [
                    {
                      "type": "Feature",
                      "id": "zone-downtown",
                      "properties": {
                        "name": "Downtown operations zone"
                      },
                      "geometry": {
                        "type": "Polygon",
                        "coordinates": [
                          [
                            [-157.884, 21.288],
                            [-157.838, 21.288],
                            [-157.838, 21.321],
                            [-157.884, 21.321],
                            [-157.884, 21.288]
                          ]
                        ]
                      }
                    }
                  ]
                }
              },
              "ops-incidents": {
                "type": "geojson",
                "data": "/__runtime-parity-showcase__/incidents.geojson",
                "promoteId": "id"
              }
            },
            "layers": [
              {
                "id": "basemap-raster",
                "type": "raster",
                "source": "basemap-grid",
                "metadata": {
                  "title": "Fixture raster lane"
                },
                "layout": {},
                "paint": {
                  "raster-opacity": 0.08
                }
              },
              {
                "id": "zone-fill",
                "type": "fill",
                "source": "ops-zones",
                "metadata": {
                  "title": "Operations zones"
                },
                "layout": {},
                "paint": {
                  "fill-color": "#72a7b8",
                  "fill-opacity": 0.22
                }
              },
              {
                "id": "zone-outline",
                "type": "line",
                "source": "ops-zones",
                "metadata": {
                  "title": "Operations zone outline"
                },
                "layout": {},
                "paint": {
                  "line-color": "#31505f",
                  "line-width": 2,
                  "line-opacity": 0.75
                }
              },
              {
                "id": "incident-halo",
                "type": "circle",
                "source": "ops-incidents",
                "metadata": {
                  "title": "Selected incident halo"
                },
                "layout": {},
                "paint": {
                  "circle-radius": [
                    "case",
                    [
                      "boolean",
                      [
                        "feature-state",
                        "selected"
                      ],
                      false
                    ],
                    18,
                    0
                  ],
                  "circle-color": "#f7d267",
                  "circle-opacity": 0.42
                }
              },
              {
                "id": "incident-points",
                "type": "circle",
                "source": "ops-incidents",
                "metadata": {
                  "title": "Incident points"
                },
                "layout": {},
                "paint": {
                  "circle-radius": [
                    "case",
                    [
                      "boolean",
                      [
                        "feature-state",
                        "selected"
                      ],
                      false
                    ],
                    9,
                    7
                  ],
                  "circle-color": [
                    "match",
                    [
                      "get",
                      "status"
                    ],
                    "Open",
                    "#d93f3f",
                    "Monitoring",
                    "#d9902f",
                    "Resolved",
                    "#2f8f75",
                    "#64748b"
                  ],
                  "circle-opacity": 0.95,
                  "circle-stroke-color": "#ffffff",
                  "circle-stroke-width": 2
                }
              }
            ]
          }
        }
        """;
}
