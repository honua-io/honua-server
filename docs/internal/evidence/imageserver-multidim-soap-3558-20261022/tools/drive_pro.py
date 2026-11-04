"""Drive licensed ArcGIS Pro (ArcPy) against one multidimensional ImageServer (#3558).

Run with Pro's Python. --service-url must point at capture_proxy.py so every
SOAP exchange is recorded. The same steps run against Honua and the Esri
reference; only the observations differ.

Persisted per step: status, ArcPy error codes (ERROR nnnnnn) and the values
ArcPy itself reports. Exception text is not persisted because it can echo
service URLs.
"""
import argparse
import datetime as dt
import json
import re
import time
import traceback
from pathlib import Path

ERROR_CODE = re.compile(r"ERROR \d{6}")


def jsonable(value):
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    if isinstance(value, dict):
        return {str(k): jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [jsonable(v) for v in value]
    if hasattr(value, "tolist"):
        return jsonable(value.tolist())
    if isinstance(value, (dt.datetime, dt.date)):
        return value.isoformat()
    return str(value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service-url", required=True)
    parser.add_argument("--variable", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True,
                        help="ArcPy workspace; keep it outside the repository")
    parser.add_argument("--window", type=int, default=4,
                        help="pixel window read from each raster; the Honua fixture is exactly 4x4")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)

    import arcpy
    import numpy as np

    arcpy.SetLogHistory(False)
    arcpy.SetLogMetadata(False)
    scratch = args.scratch
    scratch.mkdir(parents=True, exist_ok=True)
    arcpy.env.workspace = str(scratch.resolve())
    arcpy.env.scratchWorkspace = str(scratch.resolve())
    arcpy.env.overwriteOutput = True

    receipt = {
        "started_at": dt.datetime.now(dt.timezone.utc).isoformat(),
        "pro_version": arcpy.GetInstallInfo()["Version"],
        "product": arcpy.ProductInfo(),
        "variable": args.variable,
        "steps": [],
    }
    state = {}

    def write():
        args.output.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    def step(name, action):
        started = time.monotonic()
        entry = {"step": name}
        try:
            entry["observed"] = jsonable(action())
            entry["status"] = "pass"
        except Exception as error:  # ArcPy raises ExecuteError, RuntimeError, ...
            entry["status"] = "fail"
            entry["error_type"] = type(error).__name__
            entry["error_codes"] = sorted(set(ERROR_CODE.findall(str(error))))
            entry["error_frame"] = traceback.extract_tb(error.__traceback__)[-1].name
        entry["seconds"] = round(time.monotonic() - started, 1)
        receipt["steps"].append(entry)
        write()
        print(json.dumps({"step": name, "status": entry["status"], "seconds": entry["seconds"]}), flush=True)
        return entry

    url = args.service_url

    def make_layer():
        result = arcpy.management.MakeImageServerLayer(url, "md_isl")
        layer = result.getOutput(0)
        state["layer"] = layer
        return {"layer_created": True, "is_raster_layer": getattr(layer, "isRasterLayer", None)}

    def raster_properties():
        raster = arcpy.Raster(url, True)
        state["raster"] = raster
        extent = raster.extent
        names = list(raster.getDimensionNames(args.variable)) if raster.variables else []
        state["dimension_names"] = names
        return {
            "is_multidimensional": raster.isMultidimensional,
            "variables": list(raster.variables or []),
            "variable_attributes": raster.getVariableAttributes(args.variable) if raster.variables else None,
            "dimension_names": names,
            "extent": [extent.XMin, extent.YMin, extent.XMax, extent.YMax],
            "spatial_reference_wkid": raster.spatialReference.factoryCode,
            "band_count": raster.bandCount,
            "pixel_type": raster.pixelType,
            "no_data_value": raster.noDataValue,
            "width": raster.width,
            "height": raster.height,
            "cell_size": [raster.meanCellWidth, raster.meanCellHeight],
        }

    def dimension_values():
        # Each dimension separately: one dimension without values must not hide the others.
        observed = {}
        for name in state.get("dimension_names", []):
            try:
                values = list(state["raster"].getDimensionValues(args.variable, name))
                state.setdefault("dimension_values", {})[name] = values
                observed[name] = {"status": "pass", "count": len(values), "values": [str(v) for v in values[:5]]}
            except Exception as error:
                observed[name] = {"status": "fail", "error_type": type(error).__name__}
        if not state.get("dimension_values"):
            raise RuntimeError("no dimension returned values")
        return observed

    def describe_service():
        info = arcpy.Describe(state.get("layer") or url)
        return {"data_type": info.dataType, "band_count": getattr(info, "bandCount", None)}

    def multidimensional_layer():
        result = arcpy.md.MakeMultidimensionalRasterLayer(url, "md_mdl", args.variable)
        return {"layer_created": bool(result.getOutput(0))}

    def array_summary(array):
        block = np.asarray(array, dtype=np.float64)
        return {"shape": list(block.shape), "values": block.tolist()}

    def raster_read():
        return array_summary(arcpy.RasterToNumPyArray(
            state["raster"], ncols=args.window, nrows=args.window, nodata_to_value=-32768.0))

    def slice_text(value):
        return value.strftime("%Y-%m-%dT%H:%M:%S") if hasattr(value, "strftime") else str(value)

    def slice_values():
        dimension, values = next(iter(state["dimension_values"].items()))
        slices = []
        for index, value in enumerate(values[:2]):
            layer = arcpy.md.MakeMultidimensionalRasterLayer(
                url, f"md_slice_{index}", args.variable, "BY_VALUES",
                dimension_values=[[dimension, slice_text(value)]]).getOutput(0)
            entry = {"dimension": dimension, "index": index, "value": slice_text(value)}
            entry.update(array_summary(arcpy.RasterToNumPyArray(
                layer, ncols=args.window, nrows=args.window, nodata_to_value=-32768.0)))
            slices.append(entry)
        return slices

    step("MakeImageServerLayer", make_layer)
    step("Describe", describe_service)
    step("RasterProperties", raster_properties)
    step("MakeMultidimensionalRasterLayer", multidimensional_layer)
    if "raster" in state:
        step("DimensionValues", dimension_values)
        step("RasterRead", raster_read)
        if state.get("dimension_values"):
            step("SliceValues", slice_values)
    receipt["finished_at"] = dt.datetime.now(dt.timezone.utc).isoformat()
    write()


if __name__ == "__main__":
    main()
