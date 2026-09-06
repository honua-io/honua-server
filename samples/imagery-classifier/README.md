# Deterministic HTTP imagery classifier

This reference backend loads one local JSON nearest-centroid model, performs real
squared-Euclidean classification over raster bands, and serves Honua's production
HTTP inference contract. It runs separately from the lean serving image; the server
continues to delegate inference through `Geoprocessing:ImageryInference`.

Run `python3 server.py model.json` in the pinned GDAL worker image, which supplies
GDAL and NumPy. For local development set provider `http` and endpoint
`http://127.0.0.1:8080/infer`. A remote deployment must place the backend behind an
authenticated HTTPS gateway. The process loads its immutable model at startup;
request model identifiers must match, and no caller-provided model is downloaded
or executed. The health endpoint and output metadata expose the loaded model hash.

The included model consumes two bands and assigns classes 11, 29 and 47 from
centroids `(2,20)`, `(8,80)` and `(14,140)`. Ties select the first class; source
nodata/mask-invalid pixels become nodata 255. Output keeps the source CRS and grid.
The required `RasterExecutionProof` test invokes this backend over actual HTTP
through `HttpImageryInferenceClient` and `ImageryInferenceJobExecutor`, then decodes
the GeoTIFF with GDAL and compares all pixels to independently derived decision
boundaries and a three-class confusion matrix. This is a numerical execution
fixture, not a trained land-cover model or an accuracy claim for real-world imagery.
