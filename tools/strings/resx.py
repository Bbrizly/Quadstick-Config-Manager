#!/usr/bin/env python3
"""Edit Strings.resx files and build the pseudo language.

  resx.py add <file.resx> < key<TAB>value lines
  resx.py pseudo <file.resx>     writes Strings.qps-ploc.resx beside it
  resx.py prefs-pseudo <preferences.json>  the same, for the preference catalog
  resx.py export fr             writes tools/strings/to-translate.txt to paste
  resx.py import fr             reads tools/strings/fr.txt back into the app

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
OUT = 'tools/strings/to-translate.txt'

# One flat key space so the whole interface is one file: app/<key> is a string
# in the app, fmt/<key> one in the reader, pref/<name>/<field> a word from the
# preference catalog. The import reads the key to know where the line goes.
# Not words at all: a .NET format pattern a translation would break.
NOT_WORDS = {'app/Main_DMMMYyyy'}

def catalog():
    import json
    out = {}
    for tag, path in (('app', APP), ('fmt', FMT)):
        _, have = load(path)
        for key, node in have.items():
            if f'{tag}/{key}' in NOT_WORDS: continue
            out[f'{tag}/{key}'] = node.find('value').text or ''
    for p in json.load(open(PREFS)):
        for f in SAID:
            if p.get(f): out[f"pref/{p['name']}/{f}"] = p[f]
        for i, o in enumerate(p.get('optionLabels') or []):
            out[f"pref/{p['name']}/option/{i}"] = o
    return out

# What to call the language in the prompt, and what it calls itself in the
# picker. A tag that is not here still works: it goes in as written.
NAMES = {
    'fr': ('French', 'Fran\u00e7ais'), 'de': ('German', 'Deutsch'),
    'es': ('Spanish', 'Espa\u00f1ol'), 'it': ('Italian', 'Italiano'),
    'pt': ('Portuguese', 'Portugu\u00eas'), 'nl': ('Dutch', 'Nederlands'),
    'pl': ('Polish', 'Polski'), 'sv': ('Swedish', 'Svenska'),
    'ru': ('Russian', '\u0420\u0443\u0441\u0441\u043a\u0438\u0439'),
    'ja': ('Japanese', '\u65e5\u672c\u8a9e'), 'ko': ('Korean', '\ud55c\uad6d\uc5b4'),
    'zh-Hans': ('Simplified Chinese', '\u7b80\u4f53\u4e2d\u6587'),
    'ar': ('Arabic', '\u0627\u0644\u0639\u0631\u0628\u064a\u0629'),
    'hi': ('Hindi', '\u0939\u093f\u0928\u094d\u0926\u0940'),
}

def export(lang):
    with open(OUT, 'w') as f:
        f.write(PROMPT.format(lang=NAMES.get(lang, (lang, lang))[0]))
        for key, value in catalog().items():
            f.write(key + '\t' + value.replace('\n', '\\n') + '\n')
    print(f'{OUT} is ready. Paste the whole file into ChatGPT.\n'
          f'Save its answer as tools/strings/{lang}.txt, then:\n'
          f'    python3 tools/strings/resx.py import {lang}')

# Deliberately forgiving. Any line with a tab and a key we know is a
# translation; everything else, chat prose, a code fence, half a file the model
# ran out of room for, is ignored. A short answer imports what it got and the
# rest stays English rather than the whole run failing.
def read_answer(path, cat):
    # A set, not a list: saying {1} twice is legal and Japanese does it.
    holes = lambda s: sorted(set(re.findall(r'\{\d[^}]*\}', s)))
    said = {}
    for line in open(path):
        key, tab, value = line.rstrip('\n').partition('\t')
        key = key.strip()
        if not (tab and key in cat and value.strip()):
            continue
        # A translation that lost or invented a {0} would crash the format
        # call at runtime. English is better than a crash.
        if holes(value) != holes(cat[key]):
            print(f'dropped {key}: placeholders differ')
            continue
        said[key] = value.replace('\\n', '\n')
    return said

def import_(lang):
    import json
    path = f'tools/strings/{lang}.txt'
    said = read_answer(path, catalog())
    for prefix, source in (('app/', APP), ('fmt/', FMT)):
        tree, have = load(source)
        root = tree.getroot()
        kept = 0
        for key, node in have.items():
            # A key with no translation is left out, so it falls back to
            # English at runtime instead of shipping as a blank label.
            if prefix + key in said:
                node.find('value').text = said[prefix + key]
                kept += 1
            else:
                root.remove(node)
        ET.indent(tree, '  ')
        target = source.replace('.resx', f'.{lang}.resx')
        tree.write(target, encoding='utf-8', xml_declaration=True)
        print(f'{target}: {kept} of {len(have)}')
    out, n = [], 0
    for p in json.load(open(PREFS)):
        row = {'name': p['name']}
        for f in SAID:
            if said.get(f"pref/{p['name']}/{f}"):
                row[f] = said[f"pref/{p['name']}/{f}"]; n += 1
        labels = [said.get(f"pref/{p['name']}/option/{i}") for i in range(len(p.get('optionLabels') or []))]
        if labels and all(labels):
            row['optionLabels'] = labels; n += len(labels)
        out.append(row)
    target = PREFS.replace('.json', f'.{lang}.json')
    with open(target, 'w') as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
        f.write('\n')
    print(f'{target}: {n} strings')
    print(f'\nOne line by hand: add ("{lang}", "{NAMES.get(lang, (lang, lang))[1]}") to '
          'Languages in src/QuadStick.App/Localization.cs. Then `make test`.')

PROMPT = """Translate the lines below into {lang}.

Every line is a key, then a tab, then English. Give me back every line, same
key, same tab, the English replaced by {lang}. Nothing else: no numbering, no
commentary, no code fence. If it is long, keep going until the last line.

What this is: the interface of a desktop app that edits configuration files for
a QuadStick, a sip-and-puff game controller used by people with quadriplegia. A
setting described wrongly can leave someone with hardware that no longer answers
them, so be accurate before you are elegant.

Rules:

- {{0}}, {{1}}, {{0:P0}} and the like are values the app fills in. Copy them
  exactly and put them where the sentence needs them.
- \\n inside a line is a line break. Keep it as the two characters \\n.
- Names of device parts, inputs, outputs and functions stay in English. The
  device compares those bytes literally: Sip, Puff, Lip Left, Button 1,
  Left joy up, SHIFT_1, Profile Name. If one appears quoted inside a sentence,
  leave it quoted in English.
- Product and file names stay: QuadStick, USB, Bluetooth, PS4, Xbox, HID,
  .csv, .qsf.
- Plain words a person actually says. Short sentences. No marketing tone.
- Labels sit in narrow columns. Keep them short.
- Error text says what happened and what to do. Say the same thing, do not
  soften it. A key ending in /risk warns about damage: keep it as strong.
- Use the formal address if the language has one (vous, Sie).
- A single ambiguous word: translate the sense the key suggests. app/...Button
  is a button, not a noun in a sentence.

"""

if __name__ == '__main__':
    cmd = sys.argv[1]
    if cmd == 'export': export(sys.argv[2])
    elif cmd == 'import': import_(sys.argv[2])
    else: {'add': add, 'pseudo': pseudo, 'prefs-pseudo': prefs_pseudo}[cmd](sys.argv[2])
