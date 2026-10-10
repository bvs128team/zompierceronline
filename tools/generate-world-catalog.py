"""Generate the reviewed catalogue constants from static extraction; no game execution."""
import pathlib, json, hashlib
root = pathlib.Path('Zompiercer_Data')
data = json.loads(pathlib.Path('ZompiercerLAN/diagnostics/world-catalogue.json').read_text(encoding='utf-8'))
scenes = {p['scene'] for p in data['playable']}
entries = {}
for trigger in data['triggers']:
    if trigger['load'] in scenes:
        entries.setdefault(trigger['load'], {})['location'] = trigger['location']
    if trigger['unload'] in scenes:
        entry = entries.setdefault(trigger['unload'], {})
        if 'file' in entry: assert entry['file'] == trigger['file']
        entry['file'] = trigger['file']
assert set(entries) == scenes and all('file' in e and 'location' in e for e in entries.values())
lines = ['// Generated from diagnostics/world-catalogue.json; review before shipping.',
         'namespace ZompiercerLAN { internal static partial class LanWorldCatalog {',
         'private static readonly Entry[] Entries = new[] {']
for scene, entry in sorted(entries.items()):
    rail_ids = ', '.join(map(str, data['scenes'][entry['file']]))
    lines.append(f'new Entry("{scene}", {entry["location"]}, new[] {{ {rail_ids} }}),')
lines += ['};', 'private static readonly string[][] Fingerprints = new[] {']
files = ['globalgamemanagers', 'level0'] + sorted({e['file'] for e in entries.values()}) + ['Managed/Assembly-CSharp.dll', 'Managed/Assembly-CSharp-firstpass.dll']
for filename in files:
    with (root / filename).open('rb') as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
    if filename in data['files']: assert digest == data['files'][filename]
    lines.append(f'new[] {{ "{filename}", "{digest}" }},')
lines += ['};', '} }', '']
pathlib.Path('ZompiercerLAN/LanWorldCatalog.Data.cs').write_text('\n'.join(lines), encoding='utf-8')
print({scene:dict(entry, rails=data['scenes'][entry['file']]) for scene, entry in sorted(entries.items())})
