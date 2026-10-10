"""Read-only extraction of shipped Unity scene metadata; never loads game code.
Requires UnityPy 1.20.26 in tools/pythondeps. Run from the game directory.
"""
import sys, struct, pathlib, json, hashlib, gc
sys.path.insert(0, str(pathlib.Path(__file__).parent / 'pythondeps'))
import UnityPy

class Fields:
    def __init__(self, data, offset=0): self.data, self.pos = data, offset
    def read(self, fmt):
        value = struct.unpack_from('<' + fmt, self.data, self.pos)
        self.pos += struct.calcsize('<' + fmt)
        return value[0] if len(value) == 1 else value
    def align(self): self.pos = (self.pos + 3) & ~3
    def string(self):
        size = self.read('i')
        assert 0 <= size <= len(self.data) - self.pos
        value = self.data[self.pos:self.pos+size].decode('utf-8')
        self.pos += size; self.align(); return value
    def mono(self):
        game_object = self.read('iq'); self.read('B'); self.align()
        self.read('iq'); self.string(); return game_object

result = {'scenes': {}, 'triggers': [], 'files': {}}
for filename in ['globalgamemanagers.assets'] + ['level' + str(i) for i in range(13)]:
    path = pathlib.Path('Zompiercer_Data') / filename
    env = UnityPy.load(str(path)); kinds = {}; rails_objects = set(); ids = {}; names = []
    for obj in list(env.objects):
        if obj.type.name != 'MonoBehaviour': continue
        key = obj.serialized_type.script_type_index
        if key not in kinds:
            pointer = obj.read(check_read=False).m_Script
            kinds[key] = pointer.read().m_ClassName if pointer.m_PathID else ''
        kind = kinds[key]
        if kind not in ('LoadTrigger','GlobalSceneManager','ObjectID','CurvySpline'): continue
        f = Fields(obj.get_raw_data()); go = f.mono()
        if kind == 'CurvySpline': rails_objects.add(go)
        elif kind == 'ObjectID': ids[go] = f.read('i')
        elif kind == 'LoadTrigger':
            f.read('B'); f.align(); load, unload = f.string(), f.string()
            location, position, rails = f.read('i'), f.read('f'), f.read('i')
            result['triggers'].append(dict(file=filename, load=load, unload=unload,
                                          location=location, position=position, rails=rails))
        elif kind == 'GlobalSceneManager':
            # Private passenger fields are not serialized. Public scene + flag precede Scenes.
            f.string(); f.read('B'); f.align()
            for _ in range(f.read('i')):
                display, scene = f.string(), f.string(); f.read('iq')
                names.append(dict(display=display, scene=scene))
    if names: result['playable'] = names
    rail_ids = sorted(ids[go] for go in rails_objects if go in ids)
    result['scenes'][filename] = rail_ids
    with path.open('rb') as stream: result['files'][filename] = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
    print(filename, 'rails=', rail_ids, 'catalogue=', names, flush=True)
    del env; gc.collect()
pathlib.Path('ZompiercerLAN/diagnostics/world-catalogue.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
