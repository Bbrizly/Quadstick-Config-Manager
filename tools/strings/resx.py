#!/usr/bin/env python3
"""Edit Strings.resx files and build the pseudo language.

  resx.py add <file.resx> < key<TAB>value lines
  resx.py pseudo <file.resx>     writes Strings.qps-ploc.resx beside it
  resx.py prefs-pseudo <preferences.json>  the same, for the preference catalog
  resx.py export <outdir>        every string a translator sees, in chunks
  resx.py import <tag> <file...> translated chunks back into Strings.<tag>.resx

The pseudo language is English, accented and padded 40%. Run the app in it
(Settings > Language > Pseudo) and anything still in plain English is a string
the migration missed; anything clipped is a layout that assumed English width.
"""
import sys, re, xml.etree.ElementTree as ET

def load(path):
    ET.register_namespace('xsd', 'http://www.w3.org/2001/XMLSchema')
    ET.register_namespace('msdata', 'urn:schemas-microsoft-com:xml-msdata')
    t = ET.parse(path)
    return t, {d.get('name'): d for d in t.getroot().findall('data')}

def add(path):
    pairs = [l.rstrip('\n').split('\t', 1) for l in sys.stdin if l.strip()]
    add_pairs(path, pairs)

def add_pairs(path, pairs):
    tree, have = load(path)
    root = tree.getroot()
    for key, value in pairs:
        node = have.get(key)
        if node is None:
            node = ET.SubElement(root, 'data')
            node.set('name', key)
            node.set('{http://www.w3.org/XML/1998/namespace}space', 'preserve')
            ET.SubElement(node, 'value')
            have[key] = node
        node.find('value').text = value
    order = {'{http://www.w3.org/2001/XMLSchema}schema': 0, 'resheader': 1, 'data': 2}
    root[:] = sorted(root, key=lambda e: (order.get(e.tag, 3), e.get('name') or ''))
    ET.indent(tree, '  ')
    tree.write(path, encoding='utf-8', xml_declaration=True)

ACCENT = str.maketrans('aeiouAEIOUcnsyzCNSYZ', 'áéíóúÁÉÍÓÚçñšýžÇÑŠÝŽ')

def pseudo(path):
    tree, have = load(path)
    for key, node in have.items():
        # {0} means something to the code, so it survives untouched.
        node.find('value').text = accent(node.find('value').text or '')
    ET.indent(tree, '  ')
    tree.write(path.replace('.resx', '.qps-ploc.resx'), encoding='utf-8', xml_declaration=True)

# preferences.json carries its own prose. Same idea, different file: only the
# words a person reads are copied out, so a translator never sees a number, a
# default, or a firmware citation.
SAID = ('label', 'unit', 'description', 'risk')

def prefs_pseudo(path):
    import json
    out = []
    for p in json.load(open(path)):
        row = {'name': p['name']}
        for f in SAID:
            if p.get(f): row[f] = accent(p[f])
        if p.get('optionLabels'): row['optionLabels'] = [accent(x) for x in p['optionLabels']]
        out.append(row)
    target = path.replace('.json', '.qps-ploc.json')
    with open(target, 'w') as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
        f.write('\n')

def accent(text):
    parts = re.split(r'(\{\d+[^}]*\})', text)
    body = ''.join(p if i % 2 else p.translate(ACCENT) for i, p in enumerate(parts))
    return f'[{body} {"x" * max(1, int(len(text) * 0.4))}]'

APP = 'src/QuadStick.App/Strings.resx'
FMT = 'src/QuadStick.Format/Strings.resx'
PREFS = 'src/QuadStick.Format/Data/preferences.json'

# One flat key space so a translator's file is one JSON object and the import
# knows where each line goes: app/<key>, fmt/<key>, pref/<name>/<field>.
def catalog():
    import json
    out = {}
    for tag, path in (('app', APP), ('fmt', FMT)):
        _, have = load(path)
        for key, node in have.items():
            out[f'{tag}/{key}'] = node.find('value').text or ''
    for p in json.load(open(PREFS)):
        for f in SAID:
            if p.get(f): out[f"pref/{p['name']}/{f}"] = p[f]
        for i, o in enumerate(p.get('optionLabels') or []):
            out[f"pref/{p['name']}/option/{i}"] = o
    return out

CHUNK = 8000  # characters, so one chunk is one answer a chat model can finish

def export(outdir):
    import json, os, textwrap
    os.makedirs(outdir, exist_ok=True)
    for f in os.listdir(outdir):
        if f.startswith('part-'): os.remove(os.path.join(outdir, f))
    part, size, parts = {}, 0, []
    for key, value in catalog().items():
        if size and size + len(value) > CHUNK:
            parts.append(part); part, size = {}, 0
        part[key] = value; size += len(value)
    if part: parts.append(part)
    for i, part in enumerate(parts, 1):
        with open(f'{outdir}/part-{i:02d}.json', 'w') as f:
            json.dump(part, f, ensure_ascii=False, indent=1)
            f.write('\n')
    open(f'{outdir}/PROMPT.md', 'w').write(PROMPT.format(n=len(parts)))
    print(f'{len(parts)} parts in {outdir}. Read {outdir}/PROMPT.md.')

def import_(tag, paths):
    import json
    words = {}
    for p in paths: words.update(json.load(open(p)))
    for prefix, path in (('app/', APP), ('fmt/', FMT)):
        tree, have = load(path)
        root = tree.getroot()
        kept = 0
        for key, node in have.items():
            said = words.get(prefix + key)
            if said is None: root.remove(node)  # falls back to English at runtime
            else: node.find('value').text = said; kept += 1
        ET.indent(tree, '  ')
        target = path.replace('.resx', f'.{tag}.resx')
        tree.write(target, encoding='utf-8', xml_declaration=True)
        print(f'{target}: {kept} of {len(have)}')
    out, n = [], 0
    for p in json.load(open(PREFS)):
        row = {'name': p['name']}
        for f in SAID:
            said = words.get(f"pref/{p['name']}/{f}")
            if said: row[f] = said; n += 1
        if p.get('optionLabels'):
            labels = [words.get(f"pref/{p['name']}/option/{i}") for i in range(len(p['optionLabels']))]
            if all(labels): row['optionLabels'] = labels; n += len(labels)
        out.append(row)
    target = PREFS.replace('.json', f'.{tag}.json')
    with open(target, 'w') as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
        f.write('\n')
    print(f'{target}: {n} strings')
    print(f'\nLast step, by hand: add ("{tag}", "<language in its own words>") to '
          'Languages in src/QuadStick.App/Localization.cs, then `make test`.')

PROMPT = """Paste this to the chat model, then feed it part-01.json ... part-{n:02d}.json
one at a time. Save each answer as <part>.<lang>.json in this folder, then:

    python3 tools/strings/resx.py import fr tools/strings/export/*.fr.json

---

You are translating the interface of a desktop app into <LANGUAGE>. The app
edits configuration files for a QuadStick, a sip-and-puff game controller used
by people with quadriplegia. Getting a setting wrong can leave someone with
hardware that no longer answers them, so accuracy beats elegance everywhere.

I will send JSON objects of key -> English text. Reply with the same object,
same keys, same order, values translated. Nothing else: no prose, no code
fence commentary, no keys added or dropped.

Rules:

- {{0}}, {{1}}, {{0:P0}} and the like are values the app fills in. Copy them
  exactly and put them where the sentence needs them.
- \\n is a line break. Keep it.
- Keep names of device parts, inputs, outputs and functions in English. The
  device compares those bytes literally: "Sip", "Puff", "Lip Left", "Button 1",
  "Left joy up", "SHIFT_1", "Profile Name". If English appears inside a
  sentence as a quoted thing on screen, leave it quoted in English.
- Keep product and file names: QuadStick, USB, Bluetooth, PS4, Xbox, HID,
  .csv, .qsf.
- Plain words a person actually says. Short sentences. No marketing tone.
- Labels are short because they sit in narrow columns. Keep them short.
- Error text tells the user what happened and what to do. Say the same thing,
  do not soften it.
- A key ending in /risk warns about damage. Keep the warning as strong.
- Use the formal address if your language has one (vous, Sie).
- If a string is a single ambiguous word, translate the sense the key suggests
  (app/ImportButton is a button, not a noun in a sentence).
"""

if __name__ == '__main__':
    cmd = sys.argv[1]
    if cmd == 'export': export(sys.argv[2])
    elif cmd == 'import': import_(sys.argv[2], sys.argv[3:])
    else: {'add': add, 'pseudo': pseudo, 'prefs-pseudo': prefs_pseudo}[cmd](sys.argv[2])
