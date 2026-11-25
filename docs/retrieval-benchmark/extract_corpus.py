#!/usr/bin/env python3
"""
Extract XML-documented C# symbols (classes/interfaces/records/structs/enums,
methods, properties) from the TokenSaver repo into a retrieval-benchmark
corpus. Each documented symbol becomes one corpus chunk; its /// <summary>
text becomes the seed for a query in queries.jsonl.

Regex/brace-matching based (no Roslyn) -- good enough for benchmark corpus
extraction, not a general-purpose C# parser.
"""
import json
import os
import re
import sys

ROOT = "/sessions/jolly-practical-goodall/mnt/TokenSaver"
SRC = os.path.join(ROOT, "src")
MAX_BODY_LINES = 140  # cap chunk size so the corpus file stays sane

DECL_RE = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'                              # attributes
    r'(?:public|internal|private|protected)\s+'
    r'(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|virtual\s+|override\s+|async\s+|new\s+|unsafe\s+)*'
    r'(?P<kind>class|interface|record|struct|enum)\s+'
    r'(?P<name>[A-Za-z_][A-Za-z0-9_]*)'
)

MEMBER_RE = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'(?:public|internal|protected|private)\s+'
    r'(?:static\s+|sealed\s+|abstract\s+|virtual\s+|override\s+|async\s+|readonly\s+|new\s+|unsafe\s+|partial\s+|extern\s+)*'
    r'(?P<rettype>[A-Za-z_][A-Za-z0-9_<>\[\],.? ]*?)\s+'
    r'(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*'
    r'(?P<paren>\([^;{=]*\))?\s*'
    r'(?P<arrow>=>)?\s*'
    r'(?P<brace>\{)?'
)


def find_doc_blocks(lines):
    """Yield (doc_start, doc_end, decl_line_idx, summary_text) for each ///
    <summary> block immediately followed by a declaration line."""
    i = 0
    n = len(lines)
    while i < n:
        if lines[i].lstrip().startswith('///'):
            doc_start = i
            while i < n and lines[i].lstrip().startswith('///'):
                i += 1
            doc_end = i - 1
            doc_text = '\n'.join(lines[doc_start:doc_end + 1])
            m = re.search(r'<summary>(.*?)</summary>', doc_text, re.S)
            if not m:
                continue
            summary = re.sub(r'\s*///\s*', ' ', m.group(1)).strip()
            summary = re.sub(r'\s+', ' ', summary)
            # skip blank lines / attributes between doc and declaration
            j = i
            while j < n and (lines[j].strip() == '' or lines[j].lstrip().startswith('[')):
                j += 1
            if j < n:
                yield doc_start, doc_end, j, summary
            i = j
        else:
            i += 1


def matching_brace_end(lines, start_idx, max_lines=MAX_BODY_LINES):
    """Given a start line containing '{' (or the next line does), find the
    line index of the matching closing brace, scanning forward."""
    depth = 0
    started = False
    end_idx = start_idx
    for k in range(start_idx, min(len(lines), start_idx + max_lines)):
        for ch in lines[k]:
            if ch == '{':
                depth += 1
                started = True
            elif ch == '}':
                depth -= 1
        end_idx = k
        if started and depth <= 0:
            return end_idx
    return end_idx  # truncated


def extract_file(path, rel_path, chunks):
    with open(path, encoding='utf-8', errors='ignore') as f:
        text = f.read()
    lines = text.split('\n')

    for doc_start, doc_end, decl_idx, summary in find_doc_blocks(lines):
        decl_line = lines[decl_idx]

        cm = DECL_RE.match(decl_line)
        if cm:
            kind = cm.group('kind')
            name = cm.group('name')
            body_start = decl_idx
            open_idx = decl_idx
            while open_idx < len(lines) and '{' not in lines[open_idx]:
                open_idx += 1
                if open_idx - decl_idx > 3:
                    break
            end_idx = matching_brace_end(lines, open_idx if open_idx < len(lines) else decl_idx)
            end_idx = min(end_idx, body_start + MAX_BODY_LINES)
            snippet = '\n'.join(lines[body_start:end_idx + 1])
            chunks.append({
                'file': rel_path,
                'kind': kind,
                'name': name,
                'start_line': body_start + 1,
                'end_line': end_idx + 1,
                'summary': summary,
                'code': snippet,
            })
            continue

        mm = MEMBER_RE.match(decl_line)
        if mm and mm.group('name')[0].isupper():
            name = mm.group('name')
            is_method = bool(mm.group('paren'))
            kind = 'method' if is_method else 'property'
            body_start = decl_idx
            if mm.group('brace') or (mm.group('paren') and '{' in decl_line):
                open_idx = decl_idx
            elif mm.group('arrow'):
                end_idx = decl_idx
                for k in range(decl_idx, min(len(lines), decl_idx + 10)):
                    end_idx = k
                    if ';' in lines[k]:
                        break
                snippet = '\n'.join(lines[body_start:end_idx + 1])
                chunks.append({
                    'file': rel_path, 'kind': kind, 'name': name,
                    'start_line': body_start + 1, 'end_line': end_idx + 1,
                    'summary': summary, 'code': snippet,
                })
                continue
            else:
                open_idx = decl_idx
                for k in range(decl_idx, min(len(lines), decl_idx + 4)):
                    if '{' in lines[k]:
                        open_idx = k
                        break
            end_idx = matching_brace_end(lines, open_idx)
            end_idx = min(end_idx, body_start + MAX_BODY_LINES)
            snippet = '\n'.join(lines[body_start:end_idx + 1])
            chunks.append({
                'file': rel_path, 'kind': kind, 'name': name,
                'start_line': body_start + 1, 'end_line': end_idx + 1,
                'summary': summary, 'code': snippet,
            })


def main():
    chunks = []
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin', 'node_modules', 'dist', '.vs')]
        for fn in filenames:
            if fn.endswith('.cs'):
                full = os.path.join(dirpath, fn)
                rel = os.path.relpath(full, ROOT).replace('\\', '/')
                try:
                    extract_file(full, rel, chunks)
                except Exception as e:
                    print(f"skip {rel}: {e}", file=sys.stderr)

    seen = set()
    out = []
    for c in chunks:
        key = (c['file'], c['name'], c['start_line'])
        if key in seen:
            continue
        seen.add(key)
        c['chunk_id'] = f"c{len(out):04d}"
        out.append(c)

    with open('/sessions/jolly-practical-goodall/mnt/outputs/corpus_full.jsonl', 'w', encoding='utf-8') as f:
        for c in out:
            f.write(json.dumps(c, ensure_ascii=False) + '\n')

    print(f"extracted {len(out)} documented chunks")
    by_kind = {}
    for c in out:
        by_kind[c['kind']] = by_kind.get(c['kind'], 0) + 1
    print(by_kind)


if __name__ == '__main__':
    main()
