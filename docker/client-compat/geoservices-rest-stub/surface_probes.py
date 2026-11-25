"""Wire probes for additional GeoServices REST compatibility surfaces."""

from __future__ import annotations

import json
import math

import httpx


def geocode_server(client: httpx.Client, services: str, locator_name: str) -> bool:
    response = client.get(f"{services}/{locator_name}/GeocodeServer", params={"f": "json"})
    metadata = response.raise_for_status().json()
    return (
        metadata["spatialReference"]["wkid"] == 4326
        and metadata["singleLineAddressField"]["name"] == "SingleLine"
        and metadata["locatorProperties"]["LocatorName"] == locator_name
        and "Geocode" in metadata["capabilities"].split(",")
    )


def geometry_service(client: httpx.Client, services: str) -> bool:
    response = client.post(
        f"{services}/Utilities/Geometry/GeometryServer/project",
        data={
            "f": "json",
            "inSR": "4326",
            "outSR": "3857",
            "geometries": json.dumps({
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 1, "y": 0}],
            }),
        },
    )
    points = response.raise_for_status().json()["geometries"]
    return (
        len(points) == 1
        and math.isclose(points[0]["x"], 111319.49, abs_tol=0.01)
        and math.isclose(points[0]["y"], 0, abs_tol=0.01)
    )


def image_server(client: httpx.Client, service: str) -> bool:
    response = client.get(
        f"{service}/ImageServer/exportImage",
        params={
            "f": "image",
            "format": "png",
            "bbox": "-122.45,37.73,-122.38,37.80",
            "bboxSR": "4326",
            "imageSR": "4326",
            "size": "32,32",
        },
    )
    response.raise_for_status()
    return (
        response.headers.get("content-type") == "image/png"
        and response.content.startswith(b"\x89PNG\r\n\x1a\n")
    )


def services_directory(client: httpx.Client, services: str, service_name: str) -> bool:
    response = client.get(services, params={"f": "json"})
    return any(
        service["name"] == service_name and service["type"] == "FeatureServer"
        for service in response.raise_for_status().json()["services"]
    )


def vector_tile_server(client: httpx.Client, service: str) -> bool:
    response = client.get(f"{service}/VectorTileServer", params={"f": "json"})
    tile_info = response.raise_for_status().json()["tileInfo"]
    if tile_info["spatialReference"]["latestWkid"] != 3857 or tile_info["lods"][0]["level"] != 0:
        return False

    response = client.get(f"{service}/VectorTileServer/tile/0/0/0.pbf")
    response.raise_for_status()
    return (
        response.headers.get("content-type") == "application/vnd.mapbox-vector-tile"
        and bool(response.content)
    )


def i3s_scene(client: httpx.Client, scene: str) -> bool:
    response = client.get(scene, params={"f": "json"})
    layers = response.raise_for_status().json()["layers"]
    if len(layers) != 1 or layers[0]["id"] != 0:
        return False

    response = client.get(f"{scene}/layers/0", params={"f": "json"})
    layer = response.raise_for_status().json()
    if layer["layerType"] != "3DObject" or layer["store"]["version"] != "1.7":
        return False

    response = client.get(f"{scene}/layers/0/nodepages/0")
    nodes = response.raise_for_status().json()["nodes"]
    return bool(nodes) and nodes[0]["index"] == 0
