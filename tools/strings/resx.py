#!/usr/bin/env python3
"""Edit Strings.resx files and build the pseudo language.

  resx.py add <file.resx> < key<TAB>value lines
  resx.py pseudo <file.resx>     writes Strings.qps-ploc.resx beside it

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
        text = node.find('value').text or ''
        # {0} and \n mean something to the code, so they survive untouched.
        parts = re.split(r'(\{\d+[^}]*\}|\\n)', text)
        body = ''.join(p if i % 2 else p.translate(ACCENT) for i, p in enumerate(parts))
        pad = 'x' * max(1, int(len(text) * 0.4))
        node.find('value').text = f'[{body} {pad}]'
    ET.indent(tree, '  ')
    tree.write(path.replace('.resx', '.qps-ploc.resx'), encoding='utf-8', xml_declaration=True)

if __name__ == '__main__':
    {'add': add, 'pseudo': pseudo}[sys.argv[1]](sys.argv[2])
