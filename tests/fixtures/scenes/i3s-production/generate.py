"""Deterministic independent B3DM/GLB fixtures for persisted I3S resource tests."""
import argparse, hashlib, json, math, pathlib, struct, zlib

ROOT = pathlib.Path(__file__).parent
LON, LAT = -157.8581, 21.3069
lon, lat = math.radians(LON), math.radians(LAT)
a, e2 = 6378137.0, 0.0066943799901413165
n = a / math.sqrt(1-e2*math.sin(lat)**2)
origin = [n*math.cos(lat)*math.cos(lon), n*math.cos(lat)*math.sin(lon), n*(1-e2)*math.sin(lat)]
transform = [-math.sin(lon), math.cos(lon), 0, 0,
             -math.sin(lat)*math.cos(lon), -math.sin(lat)*math.sin(lon), math.cos(lat), 0,
             math.cos(lat)*math.cos(lon), math.cos(lat)*math.sin(lon), math.sin(lat), 0,
             *origin, 1]
region = [math.radians(LON-.003),math.radians(LAT-.003),math.radians(LON+.003),math.radians(LAT+.003),0,80]

def packed_json(value, alignment=4):
    data=json.dumps(value,ensure_ascii=False,separators=(',',':')).encode()
    return data+b' '*((-len(data))%alignment)

def png():
    def chunk(kind,data):
        return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data))
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',2,2,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(b'\0\xff\0\0\0\xff\0\0\0\0\xff\xff\xff\0'))+chunk(b'IEND',b'')

def glb(structural=False,textured=False,multiple=False):
    # glTF Y-up: these two CCW triangles become east/north/up after the standard Y->Z rotation.
    positions=[-20,10,20, 20,10,20, 0,50,-20, 50,10,20, 90,10,20, 70,35,-20]
    binary=bytearray(); views=[]; accessors=[]
    def view(data):
        binary.extend(b'\0'*((-len(binary))%4));i=len(views)
        views.append({'buffer':0,'byteOffset':len(binary),'byteLength':len(data)});binary.extend(data);return i
    def accessor(data,component,count,kind):
        i=len(accessors);accessors.append({'bufferView':view(data),'componentType':component,'count':count,'type':kind});return i
    attrs={'POSITION':accessor(struct.pack('<18f',*positions),5126,6,'VEC3'),
           '_FEATURE_ID_0' if structural else '_BATCHID':accessor(bytes([0,0,0,1,1,1]),5121,6,'SCALAR')}
    primitive={'attributes':attrs,'mode':4,'indices':accessor(struct.pack('<6H',0,1,2,3,4,5),5123,6,'SCALAR'),'material':0}
    material={'doubleSided':True,'pbrMetallicRoughness':{'baseColorFactor':[1,0.2,0.1,1],'metallicFactor':0,'roughnessFactor':1}}
    doc={'asset':{'version':'2.0'},'scene':0,'scenes':[{'nodes':[0]}],'nodes':[{'mesh':0}],
         'meshes':[{'primitives':[primitive]}],'accessors':accessors,'bufferViews':views,'materials':[material]}
    if structural:
        primitive['extensions']={'EXT_mesh_features':{'featureIds':[{'attribute':0,'featureCount':2,'propertyTable':0}]}}
        strings=['Honolulu café','Second building']; values=''.join(strings).encode();offsets=[0,len(strings[0].encode()),len(values)]
        properties={'OBJECTID':{'values':view(struct.pack('<2i',42,77))},'height':{'values':view(struct.pack('<2d',50,35))},
                    'name':{'values':view(values),'stringOffsets':view(struct.pack('<3I',*offsets))}}
        doc['extensions']={'EXT_structural_metadata':{'schema':{'id':'fixture','classes':{'building':{'properties':{
            'OBJECTID':{'type':'SCALAR','componentType':'INT32'},'height':{'type':'SCALAR','componentType':'FLOAT64'},'name':{'type':'STRING'}}}}},
            'propertyTables':[{'class':'building','count':2,'properties':properties}]}}
        doc['extensionsUsed']=['EXT_mesh_features','EXT_structural_metadata']
    if textured:
        attrs['TEXCOORD_0']=accessor(struct.pack('<12f',0,0,1,0,.5,1,0,0,1,0,.5,1),5126,6,'VEC2')
        doc['images']=[{'bufferView':view(png()),'mimeType':'image/png'}]
        doc['textures']=[{'source':0}];material['pbrMetallicRoughness']['baseColorTexture']={'index':0}
    if multiple:
        primitive['indices']=accessor(struct.pack('<3H',0,1,2),5123,3,'SCALAR')
        second=dict(primitive)
        second['indices']=accessor(struct.pack('<3H',3,4,5),5123,3,'SCALAR')
        second['material']=1
        doc['meshes'][0]['primitives'].append(second)
        doc['materials'].append({'doubleSided':True,'pbrMetallicRoughness':{'baseColorFactor':[0,0,1,1],'metallicFactor':0,'roughnessFactor':1}})
    binary.extend(b'\0'*((-len(binary))%4));doc['buffers']=[{'byteLength':len(binary)}]
    js=packed_json(doc);return struct.pack('<4sII',b'glTF',2,12+8+len(js)+8+len(binary))+struct.pack('<I4s',len(js),b'JSON')+js+struct.pack('<I4s',len(binary),b'BIN\0')+binary

def b3dm(names=None):
    feature=packed_json({'BATCH_LENGTH':2},8)
    batch=packed_json({'OBJECTID':[42,77],'name':['Honolulu café','Second building'] if names is None else names,'height':[50,35]},8)
    # B3DM's GLB must start at an 8-byte offset from the container beginning.
    feature+=b' '*((-(28+len(feature)))%8)
    content=glb();return struct.pack('<4s6I',b'b3dm',1,28+len(feature)+len(batch)+len(content),len(feature),0,len(batch),0)+feature+batch+content

ROOT.joinpath('tiles').mkdir(exist_ok=True)
ROOT.joinpath('tiles/0.b3dm').write_bytes(b3dm())
ROOT.joinpath('tiles/null.b3dm').write_bytes(b3dm([None,'']))
ROOT.joinpath('tiles/1.glb').write_bytes(glb(structural=True,textured=True))
ROOT.joinpath('tiles/multi.glb').write_bytes(glb(structural=True,textured=True,multiple=True))
tileset={'asset':{'version':'1.1'},'geometricError':200,'root':{'boundingVolume':{'region':region},'geometricError':100,'refine':'REPLACE','transform':transform,
    'children':[{'boundingVolume':{'region':region},'geometricError':0,'content':{'uri':'tiles/0.b3dm'}},
                {'boundingVolume':{'region':region},'geometricError':0,'transform':[1,0,0,0,0,1,0,0,0,0,1,0,150,0,0,1],'content':{'uri':'tiles/1.glb'}}]}}
ROOT.joinpath('tileset.json').write_text(json.dumps(tileset,indent=2)+'\n',encoding='utf-8')
ROOT.joinpath('oracle.json').write_text(json.dumps({'longitude':LON,'latitude':LAT,'tile_transform':transform,'gltf_y_up_positions':[-20,10,20,20,10,20,0,50,-20,50,10,20,90,10,20,70,35,-20],
    'node_east_offsets':[0,150],'feature_ids':[42,77],'height':[50,35],'name':['Honolulu café','Second building'],'texture':'2x2 PNG red/green/blue/yellow',
    'texture_sha256':hashlib.sha256(png()).hexdigest()},indent=2)+'\n',encoding='utf-8')

parser=argparse.ArgumentParser()
parser.add_argument('--update-shipping',action='store_true')
args=parser.parse_args()
if args.update_shipping:
    shipping=ROOT.parents[3]/'src/Honua.Server/fixtures/scenes/downtown-honolulu'
    shipping.joinpath('tiles/0.b3dm').write_bytes(b3dm())
    doc=json.loads(shipping.joinpath('tileset.json').read_text())
    doc['asset']['tilesetVersion']='persisted-geometry-v2'
    doc['root']['transform']=transform
    shipping.joinpath('tileset.json').write_text(json.dumps(doc,indent=2)+'\n')
