#!/usr/bin/env python3
"""Move one file's user-facing text into Strings.resx.

  migrate.py join <file.cs>               glue split sentences back into one
  migrate.py plan <file.cs> <Prefix>      what it would do, as key<TAB>text
  migrate.py apply <file.cs> <Prefix>     do it, and add the keys to the resx

Finds the same text LocalizationTests looks for, names each one after its first
few words, and rewrites the call site. An interpolated string becomes a
string.Format with {0}, {1} in the order the expressions appear.

It stops rather than guesses. A sentence split across two literals prints as
JOIN and blocks the run, because a translator has to be able to reorder the
whole sentence. A verbatim string, a format specifier, or a hole holding its
own string prints as SKIP and is left alone.
"""
import re, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import resx

SINK_TAIL = re.compile(
    r'(?:\b(?:Text|Content|Title|Header|Watermark)\s*=\s*'
    r'|(?:SetName|SetHelpText|SetTip)\([^,]*,\s*'
    r'|\b(?:Field|Heading|Label|Caption|LinkButton|ShowHelp|ConfirmAsync|Status)\(\s*)$')
PROSE = re.compile(r'[a-zA-Z] [a-z]')

# C# has five ways to write a string and they nest, so a regex cannot find
# them: $"{(a ? "x" : "y")} of {n}" is one literal holding two more. This walks
# the file the way the compiler does and returns the outermost ones.
def literals(src):
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if c == '/' and src[i+1:i+2] == '/':
            i = src.find('\n', i);  i = n if i < 0 else i;  continue
        if c == '/' and src[i+1:i+2] == '*':
            i = src.find('*/', i) + 2;  continue
        if c == "'":
            i += 3 if src[i+1:i+2] == '\\' else 2
            while i < n and src[i-1] != "'": i += 1
            continue
        if c == '"' or (c in '$@' and re.match(r'[$@]{1,2}"', src[i:i+3])):
            start = i
            end = read_string(src, i)
            if end is None: return out          # something this cannot read: stop here
            out.append((start, end))
            i = end
            continue
        i += 1
    return out

def read_string(src, i):
    n = len(src)
    prefix = ''
    while src[i] in '$@': prefix += src[i]; i += 1
    if src[i] != '"': return None
    if src[i:i+3] == '"""': return None          # raw string, not used here
    verbatim, interpolated = '@' in prefix, '$' in prefix
    i += 1
    while i < n:
        c = src[i]
        if verbatim and c == '"':
            if src[i+1:i+2] == '"': i += 2; continue
            return i + 1
        if not verbatim and c == '\\': i += 2; continue
        if not verbatim and c == '"': return i + 1
        if interpolated and c == '{':
            if src[i+1:i+2] == '{': i += 2; continue
            i = read_hole(src, i + 1)
            if i is None: return None
            continue
        if interpolated and c == '}' and src[i+1:i+2] == '}': i += 2; continue
        i += 1
    return None

# Everything between { and its matching }, strings inside it included.
def read_hole(src, i):
    depth, n = 1, len(src)
    while i < n:
        c = src[i]
        if c == '"' or (c in '$@' and re.match(r'[$@]{1,2}"', src[i:i+3])):
            i = read_string(src, i)
            if i is None: return None
            continue
        if c == '{': depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0: return i + 1
        i += 1
    return None

# "half a sentence " + "and the rest" is one sentence to a translator, who has
# to be free to put its words in another order. Glue the pieces back together
# before anything else looks at them.
def join_split_strings(src):
    while True:
        spans = literals(src)
        merged = False
        for (s1, e1), (s2, e2) in zip(spans, spans[1:]):
            gap = src[e1:s2]
            if gap.strip() != '+' or '//' in gap: continue
            a, b = src[s1:e1], src[s2:e2]
            if a.startswith('@') or b.startswith('@'): continue
            head = '$' if a.startswith('$') or b.startswith('$') else ''
            body = a[a.index('"') + 1:-1] + b[b.index('"') + 1:-1]
            src = src[:s1] + head + '"' + body + '"' + src[e2:]
            merged = True
            break
        if not merged: return src

def line_of(src, pos): return src.count('\n', 0, pos) + 1

def candidates(src):
    out = []
    for start, end in literals(src):
        lit = src[start:end]
        if not re.search(r'[A-Za-z]', lit): continue
        line_start = src.rfind('\n', 0, start) + 1
        if 'throw ' in src[line_start:start]: continue
        if PROSE.search(lit) or SINK_TAIL.search(src[line_start:start]):
            out.append((start, end, lit))
    return out

def key_for(prefix, text, taken):
    words = re.findall(r'[A-Za-z0-9]+', text)[:5]
    stem = ''.join(w[:1].upper() + w[1:] for w in words) or 'Text'
    key, n = f'{prefix}_{stem}', 2
    while key in taken: key, n = f'{prefix}_{stem}{n}', n + 1
    taken.add(key)
    return key

# "you have {n} of {m}" -> ("you have {0} of {1}", ["n", "m"])
def split_interpolation(body):
    out, args, i = '', [], 0
    while i < len(body):
        c = body[i]
        if c == '{' and body[i+1:i+2] == '{': out += '{{'; i += 2; continue
        if c == '}' and body[i+1:i+2] == '}': out += '}}'; i += 2; continue
        if c == '}': return None
        if c != '{': out += c; i += 1; continue
        j = read_hole(body, i + 1)
        if j is None: return None
        expr = body[i+1:j-1]
        if ':' in expr or '"' in expr or top_level_comma(expr): return None  # a format spec, or an alignment
        out += '{%d}' % len(args)
        args.append(expr)
        i = j
    return out, args

# A comma inside Foo(a, b) is that call's business. One outside every bracket
# is C#'s alignment syntax, which does not survive the move to {0}.
def top_level_comma(expr):
    depth = 0
    for c in expr:
        if c in '([{': depth += 1
        elif c in ')]}': depth -= 1
        elif c == ',' and depth == 0: return True
    return False

# The body of a C# literal is not its text: \" is one character, and a resx
# holds the character.
def unescape(body):
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', 'r': '\r'}.get(m.group(1), m.group(1)), body)

def rewrite(lit, key):
    if lit.startswith('@') or lit.startswith('$@') or lit.startswith('@$'): return None, None
    if not lit.startswith('$'): return unescape(lit[1:-1]), f'Strings.{key}'
    split = split_interpolation(lit[2:-1])
    if split is None: return None, None
    text, args = unescape(split[0]), split[1]
    if not args: return text, f'Strings.{key}'
    return text, 'string.Format(CultureInfo.CurrentCulture, Strings.%s, %s)' % (key, ', '.join(args))

def run(mode, path, prefix):
    src = open(path).read()
    found = candidates(src)
    joined = [f'{line_of(src, s)}: {lit}' for s, e, lit in found
              if re.match(r'\s*(\+|\)|,)?\s*\+?\s*[$@]*"', src[e:e+40].lstrip()) and src[e:e+40].lstrip().startswith('+')
              or re.search(r'"\s*\+\s*$', src[max(0, s-40):s])]
    taken, rows, skips, seen = set(), [], [], {}
    for start, end, lit in found:
        probe = lit[2:-1] if lit.startswith('$') else lit[1:-1]
        key = seen.get(lit) or key_for(prefix, probe, taken)
        text, expr = rewrite(lit, key)
        if text is None:
            skips.append(f'{line_of(src, start)}: {lit}')
            if lit not in seen: taken.discard(key)
            continue
        seen[lit] = key
        rows.append((start, end, key, text, expr))

    for j in joined: print('JOIN', j)
    for s in skips: print('SKIP', s)
    for _, _, key, text, _ in rows: print(f'{key}\t{text}')
    if mode != 'apply': return
    if joined: sys.exit('Join these into one string first: a sentence split in two cannot be reordered.')

    for start, end, _, _, expr in sorted(rows, reverse=True):
        src = src[:start] + expr + src[end:]
    # A const cannot hold a resource: its value is not known until the app
    # knows what language it is being read in.
    src = re.sub(r'\bconst string (\w+\s*=\s*\n?\s*(?:Strings\.|string\.Format\())', r'static readonly string \1', src)
    if 'string.Format(' in src and 'using System.Globalization;' not in src:
        src = re.sub(r'^(using )', 'using System.Globalization;\n\\1', src, count=1, flags=re.M)
    open(path, 'w').write(src)
    resx.add_pairs(os.path.join(os.path.dirname(path), 'Strings.resx'),
                   [(k, t) for _, _, k, t, _ in rows])
    print(f'moved {len(rows)}, left {len(skips)}')

if __name__ == '__main__':
    if sys.argv[1] == 'join':
        path = sys.argv[2]
        joined = join_split_strings(open(path).read())
        open(path, 'w').write(joined)
    else:
        run(sys.argv[1], sys.argv[2], sys.argv[3])
