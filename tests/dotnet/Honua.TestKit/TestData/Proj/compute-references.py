"""Reproduce the datum HTTP fixture oracle outside Honua and PostGIS."""

import os
from pathlib import Path
import shutil
import tempfile


with tempfile.TemporaryDirectory(prefix="honua-proj-reference-") as temporary:
    os.environ["PROJ_NETWORK"] = "OFF"
    os.environ["PROJ_USER_WRITABLE_DIRECTORY"] = temporary

    import pyproj

    data = Path(temporary) / "data"
    data.mkdir()
    shutil.copyfile(Path(pyproj.datadir.get_data_dir()) / "proj.db", data / "proj.db")
    pyproj.datadir.set_data_dir(str(data))
    pyproj.network.set_network_enabled(False)
    print(f"pyproj={pyproj.__version__} PROJ={pyproj.proj_version_str}; network=OFF")
    for source, target in ((4267, 4269), (4269, 4267)):
        transform = pyproj.Transformer.from_crs(source, target, always_xy=True)
        print("grid-free", source, target, transform.transform(-100, 40, 12))

    grid = Path(__file__).resolve().with_name("us_noaa_conus.tif")
    transform = pyproj.Transformer.from_pipeline(
        "+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad "
        f"+step +proj=hgridshift +grids={grid} "
        "+step +proj=unitconvert +xy_in=rad +xy_out=deg"
    )
    print("NADCON", 4267, 4269, transform.transform(-100, 40, 12))
    print("NADCON", 4269, 4267, transform.transform(
        -100, 40, 12, direction=pyproj.enums.TransformDirection.INVERSE))
