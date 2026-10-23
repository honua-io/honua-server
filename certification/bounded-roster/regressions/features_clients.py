#!/usr/bin/env python3
"""Exercise #4994/#4995 with real clients and client-compat-v1.sql's oracle.

Run in ghcr.io/osgeo/gdal:ubuntu-small-3.8.4 or qgis/qgis:3.44.13 on the
isolated client-compat network. Output is a focused regression receipt, not
a replacement for the governed full-roster certification envelopes.
"""
import argparse
import json
import os
import re
from urllib.parse import parse_qs, urlsplit


def check_discovery(requests):
    assert any(urlsplit(url).path.endswith("/collections/0/queryables") for url in requests), requests
    filters = [url for url in requests if "filter" in parse_qs(urlsplit(url).query)]
    assert filters, "Client never pushed the attribute filter to the server"
    return filters


def gdal_checks(base):
    from osgeo import gdal

    gdal.UseExceptions()
    assert gdal.VersionInfo("RELEASE_NAME") == "3.8.4"
    requests = []

    def capture(_level, _number, message):
        if "Fetch(" in message:
            match = re.search(r"https?://[^\s)]+", message)
            if match:
                requests.append(match.group())

    gdal.SetConfigOption("CPL_DEBUG", "ON")
    gdal.PushErrorHandler(capture)
    dataset = gdal.OpenEx("OAPIF:" + base + "/ogc/features/collections/0", gdal.OF_VECTOR)
    assert dataset is not None
    layer = dataset.GetLayer(0)
    feature = layer.GetFeature(3)
    assert feature is not None
    geometry = feature.GetGeometryRef()
    point = (geometry.GetX(), geometry.GetY())
    assert point == (-122.46, 37.73), point
    assert feature.GetField("name") == "gamma"
    assert any(urlsplit(url).path.endswith("/items/3") for url in requests), requests
    assert layer.SetAttributeFilter("status = 'active'") == 0
    selected = sorted(feature.GetFID() for feature in layer)
    assert selected == [1, 3, 5, 7, 9], selected
    filters = check_discovery(requests)
    gdal.PopErrorHandler()
    return {"client": "GDAL/OGR 3.8.4", "point": point, "selected_ids": selected,
            "filter_requests": filters, "requests": requests}


def qgis_checks(base):
    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
    from qgis.core import Qgis, QgsApplication, QgsFeatureRequest, QgsNetworkAccessManager, QgsVectorLayer
    from qgis.PyQt.QtNetwork import QNetworkRequest

    assert Qgis.QGIS_VERSION.startswith("3.44.13"), Qgis.QGIS_VERSION
    app = QgsApplication([], False)
    app.initQgis()
    exchanges = []

    def capture(reply):
        exchanges.append({"url": reply.request().url().toString(),
                          "status": reply.attribute(QNetworkRequest.HttpStatusCodeAttribute)})

    QgsNetworkAccessManager.instance().finished.connect(capture)
    layer = QgsVectorLayer(f"url='{base}/ogc/features' typename='0'", "fixture", "OAPIF")
    assert layer.isValid(), layer.error().message()
    selected = sorted(feature["name"] for feature in layer.getFeatures(
        QgsFeatureRequest().setFilterExpression("status = 'active'")))
    assert selected == ["alpha", "epsilon", "eta", "gamma", "iota"], selected
    app.processEvents()
    requests = [exchange["url"] for exchange in exchanges if exchange["status"] == 200]
    filters = check_discovery(requests)
    # Keep the provider alive until all network completion signals have drained.
    return {"client": "QGIS " + Qgis.QGIS_VERSION, "selected_names": selected,
            "filter_requests": filters, "exchanges": exchanges}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("client", choices=["gdal", "qgis"])
    parser.add_argument("--base-url", default="http://honua:5000")
    args = parser.parse_args()
    result = (gdal_checks if args.client == "gdal" else qgis_checks)(args.base_url.rstrip("/"))
    print(json.dumps({"result": "pass", "fixture": "tests/seed/client-compat-v1.sql", **result}, indent=2))
