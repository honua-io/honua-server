"""Regenerate the PyArrow-authored GeoParquet import controls.

Use a Python environment with pyarrow 25.0.1 and shapely 2.2.0. These files are
checked in so .NET tests can exercise an independently authored input without
adding Python to the test runner.
"""

import json
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq
from shapely.geometry import Point
from shapely.wkb import dumps


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
CATALOG = json.loads((ROOT / "src/Honua.Core/Features/Infrastructure/Crs/Resources/geoparquet-crs-projjson.json").read_text())


def write(name: str, version: str, point: Point, *, epoch: float | None = None) -> None:
    columns = {
        "geometry": {
            "encoding": "WKB",
            "geometry_types": ["Point Z" if point.has_z else "Point"],
        }
    }
    if epoch is not None:
        columns["geometry"]["crs"] = CATALOG["crs"]["7912"]
        columns["geometry"]["epoch"] = epoch
    metadata = {
        "version": version,
        "primary_column": "geometry",
        "columns": columns,
        "vendor_extension": {"independent_author": "PyArrow"},
    }
    table = pa.table({
        "objectid": pa.array([1], type=pa.int64()),
        "name": pa.array(["PyArrow control"], type=pa.string()),
        "geometry": pa.array([dumps(point, output_dimension=3 if point.has_z else 2)], type=pa.binary()),
    })
    table = table.replace_schema_metadata({b"geo": json.dumps(metadata, separators=(",", ":")).encode()})
    pq.write_table(table, HERE / name, compression="NONE", row_group_size=1, version="2.6")


write("independent-v1.0.parquet", "1.0.0", Point(-122.4194, 37.7749))
write("independent-epoch-v1.1.parquet", "1.1.0", Point(10, 20, 30), epoch=2021.25)
