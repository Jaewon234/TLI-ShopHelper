#!/usr/bin/env python3
"""
부귀(Magnificent)·숭고(Noble) 보조 스킬의 게임 번호 → 한국어 이름 표를 tlidb.com 에서 만든다.
  python3 tools/build_skill_names.py src/Core/Models/skill_names.json
목록 페이지 2개에서 상세 페이지 주소를 모으고, 상세 페이지마다 'id: 숫자' 를 읽는다.
사이트에 부담이 가지 않게 요청 사이에 1.2초씩 쉰다. 새 시즌에 다시 돌리면 표가 갱신된다.
"""
import json, re, sys, time, urllib.request

BASE = "https://tlidb.com/ko/"
LISTS = {"noble": "Noble_Support_Skill", "magnificent": "Magnificent_Support_Skill"}
SUFFIX = {"noble": "_Noble", "magnificent": "_Magnificent"}
UA = {"User-Agent": "Mozilla/5.0 (ShopHelper skill name table builder)"}

def get(path):
    for attempt in range(4):
        try:
            req = urllib.request.Request(BASE + path, headers=UA)
            with urllib.request.urlopen(req, timeout=30) as r:
                return r.read().decode("utf-8")
        except Exception as e:
            print(f"  다시 시도 {attempt + 1}: {path} ({e})", file=sys.stderr)
            time.sleep(3 * (attempt + 1))
    raise RuntimeError("못 읽음: " + path)

def main(out):
    pages = {}   # href -> (kind, name)
    for kind, page in LISTS.items():
        html = get(page)
        for href, name in re.findall(r'href="([A-Za-z0-9_]+' + SUFFIX[kind] + r')">([^<]+)</a>', html):
            pages.setdefault(href, (kind, name.strip()))
        time.sleep(1.2)
    print(f"상세 페이지 {len(pages)}개", file=sys.stderr)
    table, missing = {}, []
    for i, (href, (kind, name)) in enumerate(sorted(pages.items()), 1):
        html = get(href)
        m = re.search(r'id:\s*(\d+)', html)
        if not m:
            missing.append(href)
        else:
            sid = m.group(1)
            if sid in table and table[sid]["name"] != name:
                print(f"  번호 겹침 {sid}: {table[sid]['name']} / {name}", file=sys.stderr)
            table[sid] = {"name": name, "kind": kind}
        if i % 20 == 0:
            print(f"  {i}/{len(pages)}", file=sys.stderr)
        time.sleep(1.2)
    data = {"source": "https://tlidb.com/ko/ (Noble_Support_Skill, Magnificent_Support_Skill)",
            "built": time.strftime("%Y-%m-%d"), "skills": dict(sorted(table.items(), key=lambda kv: int(kv[0])))}
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    print(f"완료: {len(table)}개, 번호 못 읽음 {len(missing)}개 {missing}", file=sys.stderr)

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "skill_names.json")
