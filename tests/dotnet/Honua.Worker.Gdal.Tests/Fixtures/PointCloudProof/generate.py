"""Write tiny LAS 1.4/point-format-3 inputs directly from declared source values."""
from pathlib import Path
import struct
from osgeo import osr

root = Path(__file__).parent

def write(name, epsg, xy, scale):
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(epsg)
    wkt = srs.ExportToWkt(['FORMAT=WKT2_2019']).encode() + b'\0'
    vlr = struct.pack('<H16sHH32s', 0, b'LASF_Projection', 2112, len(wkt), b'OGC WKT') + wkt
    header = bytearray(375)
    struct.pack_into('<4sHH', header, 0, b'LASF', 7, 16)
    struct.pack_into('<BB32s32sHHHII', header, 24, 1, 4, b'Honua analytical fixture', b'Python struct', 1, 2026, 375, 375+len(vlr), 1)
    struct.pack_into('<BHI', header, 104, 3, 34, 3)
    struct.pack_into('<5I', header, 111, 2, 1, 0, 0, 0)
    struct.pack_into('<3d3d', header, 131, scale, scale, .001, 0, 0, 0)
    zs = [12.345, -7.125, 1234.5]
    points = bytearray()
    for i, ((x, y), z) in enumerate(zip(xy,zs)):
        # Fixed nonzero dimensions catch default-writer attribute loss.
        points += struct.pack('<iiiHBBbBHdHHH', round(x/scale), round(y/scale), round(z/.001),
                              [10,60000,123][i], [9,26,17][i], [2,6,9][i], [-3,0,7][i],
                              [1,2,3][i], [40,41,42][i], 123456.25+i*.5,
                              [65535,1234,0][i], [0,5678,65535][i], [123,9012,456][i])
    struct.pack_into('<6d', header, 179, max(x for x,y in xy), min(x for x,y in xy), max(y for x,y in xy), min(y for x,y in xy), max(zs), min(zs))
    struct.pack_into('<Q', header, 247, 3)
    struct.pack_into('<15Q', header, 255, 2,1,*([0]*13))
    (root/name).write_bytes(header+vlr+points)

write('geographic.las',4326,[(-155.1234567,19.7654321),(-155.1234999,19.7654999),(-155.1234001,19.7654001)],1e-7)
write('mercator.las',3857,[(500000,0),(611319.491,111325.143),(277361.018,-111325.143)],.001)

# Valid LAS 1.4 format-10 records with no waveform packet attached (descriptor 0).
# The source literals include extended returns/classification and NIR.
base = (root/'mercator.las').read_bytes()
offset = struct.unpack_from('<I',base,96)[0]
header = bytearray(base[:offset])
struct.pack_into('<BHI',header,104,10,67,0)
struct.pack_into('<5I',header,111,0,0,0,0,0)
struct.pack_into('<15Q',header,255,1,1,0,0,0,0,0,0,1,0,0,0,0,0,0)
points = bytearray()
for i, (x,y,z) in enumerate([(500000,0,12.345),(611319.491,111325.143,-7.125),(277361.018,-111325.143,1234.5)]):
    points += struct.pack('<iiiHBBBBhHdHHHH',round(x/.001),round(y/.001),round(z/.001),
                          [10,60000,123][i],[0xA9,0x32,0x21][i],[0x28,0,0][i],[42,6,9][i],i+1,
                          [-500,0,1167][i],40+i,123456.25+i*.5,
                          [65535,1234,0][i],[0,5678,65535][i],[123,9012,456][i],1000*(i+1))
    points += bytes(29)
(root/'waveform-10.las').write_bytes(header+points)

for fmt, length, prefix in [(4,57,28),(5,63,34),(9,59,30)]:
    waveform_header = bytearray(header if fmt >= 6 else base[:offset])
    struct.pack_into('<BH',waveform_header,104,fmt,length)
    source_records, stride = (points,67) if fmt >= 6 else (base[offset:],34)
    waveform_points = b''.join(source_records[i*stride:i*stride+prefix]+bytes(29) for i in range(3))
    (root/f'waveform-{fmt}.las').write_bytes(waveform_header+waveform_points)
