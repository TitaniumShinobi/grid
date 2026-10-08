"""Pin GTA V Enhanced Location reference snapshots via MediaWiki APIs.

Read-only acquisition. ``--verify-only`` checks the pinned v2 digests. ``crawl`` performs
bounded, exhaustive discovery of Location-bearing reference pages: it seeds from the
native zone table (game-file mirrored names) and product title pages, follows game-scoped
place categories, infobox/lead containers and place-headed member lists until the frontier
is empty, and writes normalized extracts, a v3 source manifest and a discovery ledger.
No place name, tree or expected count is encoded here; place recognition uses generic
English place nouns only.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import re
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

MANIFEST = Path(__file__).with_name("gta_v_enhanced_location_hierarchy_sources.v2.json")
MANIFEST_V3 = Path(__file__).with_name("gta_v_enhanced_location_hierarchy_sources.v3.json")
DISCOVERY = Path(__file__).with_name("gta_v_enhanced_location_discovery.v1.json")
API = "https://www.grandtheftwiki.com/api.php"
REFERENCES = Path(__file__).with_name("references")
CORPUS = REFERENCES / "location-corpus"
_POOL = concurrent.futures.ThreadPoolExecutor(max_workers=8, thread_name_prefix="acquire")

# Generic English place nouns. Used only to decide which pages/categories to examine next;
# semantic levels are assigned later by registration, not here.
PLACE_NOUNS = {
    "airport", "airfield", "airstrip", "apartment", "apartments", "arena", "area", "areas", "avenue", "bank", "bar",
    "base", "bay", "beach", "beaches", "border", "borough", "boulevard", "bridge", "bridges", "building",
    "buildings", "business", "businesses", "camp", "canal", "canals", "canyon", "casino", "cave", "cemetery",
    "church", "cities", "city", "club", "coast", "community", "complex", "compound", "counties", "country",
    "county", "cove", "dam", "desert", "district", "districts", "dock", "docks", "drive", "estate", "facility",
    "factory", "farm", "field", "fields", "forest", "freeway", "garage", "geography", "golf", "harbor", "harbour",
    "headquarters", "highway", "hill", "hills", "home", "homes", "hospital", "hospitals", "hotel", "house",
    "houses", "interior", "interiors", "island", "islands", "lake", "landmark", "landmarks", "lane", "lighthouse",
    "location", "locations", "mall", "mansion", "marina", "market", "mine", "motel", "mountain", "mountains",
    "municipality", "nation", "neighborhood", "neighborhoods", "neighbourhood", "neighbourhoods", "ocean",
    "office", "offices", "park", "parks", "peak", "pier", "place", "places", "plant", "plaza", "point", "points",
    "port", "prison", "property", "properties", "province", "quarry", "racetrack", "ranch", "range", "region",
    "regions", "reservoir", "residence", "resort", "restaurant", "restaurants", "river", "road", "roads", "room",
    "rooms", "route", "safehouse", "safehouses", "school", "sea", "settlement", "settlements", "shop", "shops",
    "skyscraper", "square", "stadium", "state", "states", "station", "stations", "store", "stores", "street",
    "streets", "structure", "structures", "studio", "studios", "suburb", "terminal", "territory", "theater",
    "theatre", "tower", "towers", "town", "towns", "track", "trail", "tunnel", "tunnels", "university", "valley",
    "village", "warehouse", "warehouses", "way", "wilderness", "yard", "zone", "zones",
}
PLACE_FIELDS = {
    "location", "locations", "city", "county", "state", "country", "district", "neighborhood", "neighbourhood",
    "area", "region", "island", "street", "address", "borough", "town", "place", "territory", "province",
}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def fetch_wikitext(title: str, oldid: int | None = None) -> str:
    params = {"action": "parse", "format": "json", "prop": "wikitext"}
    if oldid is not None:
        params["oldid"] = str(oldid)
    else:
        params["page"] = title
    url = API + "?" + urllib.parse.urlencode(params)
    with urllib.request.urlopen(url, timeout=60) as response:
        payload = json.load(response)
    if "error" in payload:
        raise RuntimeError(payload["error"]["info"])
    return payload["parse"]["wikitext"]["*"]


def strip_markup(text: str) -> str:
    text = re.sub(r"\[\[([^|\]]+)\|([^\]]+)\]\]", r"\2", text)
    text = re.sub(r"\[\[([^\]]+)\]\]", r"\1", text)
    text = re.sub(r"'''([^']+)'''", r"\1", text)
    text = re.sub(r"''([^']+)''", r"\1", text)
    text = re.sub(r"\s+", " ", text)
    return text.strip()


def verify(path: Path, expected: str) -> None:
    digest = sha256(path.read_bytes())
    if digest != expected:
        raise SystemExit(f"Digest mismatch for {path.name}: expected {expected}, got {digest}")


def words(text: str) -> set[str]:
    return set(re.findall(r"[a-z]+", text.lower()))


def has_place_noun(text: str) -> bool:
    return bool(words(text) & PLACE_NOUNS)


class ScopeMatcher:
    def __init__(self, markers: list[str]) -> None:
        # "GTA V" must not match "GTA Vice City": a marker ends at a non-word boundary.
        self.patterns = [re.compile(r"(?<![\w])" + re.escape(m) + r"(?![\w])") for m in markers]

    def matches(self, text: str) -> bool:
        return any(p.search(text) for p in self.patterns)


def balanced_template_end(text: str, start: int) -> int:
    depth = 0
    i = start
    while i < len(text) - 1:
        pair = text[i:i + 2]
        if pair == "{{":
            depth += 1
            i += 2
            continue
        if pair == "}}":
            depth -= 1
            i += 2
            if depth == 0:
                return i
            continue
        i += 1
    return len(text)


def clean(text: str) -> str:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    text = re.sub(r"<ref[^>/]*/>", "", text)
    text = re.sub(r"<ref[^>]*>.*?</ref>", "", text, flags=re.S)
    return text


def leading_templates(text: str) -> tuple[list[str], int]:
    """Top-of-page templates (infoboxes, hatnotes) and the offset where prose begins."""
    templates: list[str] = []
    i = 0
    while True:
        while i < len(text) and text[i] in " \n\t":
            i += 1
        if text.startswith("{{", i):
            end = balanced_template_end(text, i)
            templates.append(text[i:end])
            i = end
            continue
        if text.startswith("[[File:", i) or text.startswith("[[Image:", i):
            depth = 0
            j = i
            while j < len(text) - 1:
                if text[j:j + 2] == "[[":
                    depth += 1
                    j += 2
                    continue
                if text[j:j + 2] == "]]":
                    depth -= 1
                    j += 2
                    if depth == 0:
                        break
                    continue
                j += 1
            i = j
            continue
        break
    return templates, i


def infobox_of(templates: list[str]) -> str | None:
    for template in templates:
        if re.match(r"\{\{\s*infobox", template, re.I):
            return template
    return None


def sections(text: str) -> list[tuple[int, str, str]]:
    """(level, heading, body) for every heading section after the lead."""
    result = []
    matches = list(re.finditer(r"(?m)^(={2,6})\s*(.*?)\s*\1\s*$", text))
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        result.append((len(match.group(1)), match.group(2).strip(), text[match.end():end].strip("\n")))
    return result


def extract(wikitext: str) -> tuple[str, dict]:
    """Deterministic normalized extract: infobox, lead prose, place-headed sections, categories."""
    text = clean(wikitext)
    templates, prose_start = leading_templates(text)
    infobox = infobox_of(templates)
    first_heading = re.search(r"(?m)^={2,6}.*?={2,6}\s*$", text[prose_start:])
    lead_end = prose_start + first_heading.start() if first_heading else len(text)
    lead = text[prose_start:lead_end]
    lead = re.sub(r"(?m)^\[\[Category:[^\]]*\]\]\s*$", "", lead).strip()
    kept_sections = []
    kept_level: int | None = None
    for level, heading, body in sections(text):
        # Subsections of a place-headed section belong to it even when their own heading is a bare name.
        nested = kept_level is not None and level > kept_level
        if has_place_noun(heading) or nested:
            if not nested:
                kept_level = level
            body = re.sub(r"(?m)^\[\[Category:[^\]]*\]\]\s*$", "", body).strip()
            kept_sections.append((level, heading, body))
        else:
            kept_level = None
    categories = sorted(set(m.strip() for m in re.findall(r"\[\[Category:([^\]|]+)", text)))
    parts = []
    if infobox:
        parts.append("[[INFOBOX]]\n" + infobox.strip())
    parts.append("[[LEAD]]\n" + lead)
    for level, heading, body in kept_sections:
        parts.append(f"[[SECTION:{level}:{heading}]]\n{body}")
    parts.append("[[CATEGORIES]]\n" + "\n".join(categories))
    normalized = "\n\n".join(parts).strip() + "\n"
    facts = {
        "infobox": infobox,
        "lead": lead,
        "sections": kept_sections,
        "categories": categories,
    }
    return normalized, facts


def link_targets(text: str) -> list[str]:
    targets = []
    for match in re.finditer(r"\[\[([^\]|#]+)(?:#[^\]|]*)?(?:\|[^\]]*)?\]\]", text):
        target = match.group(1).strip()
        if not target or target.startswith(":") or ":" in target.split("/")[0] and re.match(
                r"^(File|Image|Category|wp|w|wikipedia|Template|User|Help|Special|Media)\s*:", target, re.I):
            continue
        targets.append(target[0].upper() + target[1:])
    return targets


def infobox_fields(infobox: str) -> dict[str, str]:
    fields: dict[str, str] = {}
    body = infobox[2:-2]
    depth = 0
    current = []
    parts = []
    i = 0
    while i < len(body):
        pair = body[i:i + 2]
        if pair in ("{{", "[["):
            depth += 1
            current.append(pair)
            i += 2
            continue
        if pair in ("}}", "]]"):
            depth -= 1
            current.append(pair)
            i += 2
            continue
        if body[i] == "|" and depth == 0:
            parts.append("".join(current))
            current = []
            i += 1
            continue
        current.append(body[i])
        i += 1
    parts.append("".join(current))
    for part in parts[1:]:
        if "=" in part:
            key, value = part.split("=", 1)
            fields[key.strip().lower()] = value.strip()
    return fields


def lead_head_phrase(lead: str) -> str:
    """The descriptor noun phrase of the first copular sentence, cut at its first preposition or clause."""
    sentence = strip_markup(re.sub(r"\{\{[^{}]*\}\}", "", lead)).split(". ")[0]
    match = re.search(r"\b(?:is|was|are|were)\s+(?:a|an|the|one of the)\s+(.{0,120})", sentence)
    if not match:
        return ""
    return re.split(r"\b(?:in|on|at|of|within|near|located|situated|found|that|which|who|appearing|featured|owned|run|and is|,)\b|[,;(]",
                    match.group(1), maxsplit=1)[0]


def games_declaration(facts: dict, scope: "ScopeMatcher", product_keys: list[str]) -> bool | None:
    """True/False when the infobox declares the games the subject appears in; None when it declares none.

    Accepts plain text ("game_1 = GTA V") matched by the product markers and flag templates
    ("{{games|V=y|O=y}}") whose flag keys the source config names as product keys."""
    if not facts["infobox"]:
        return None
    declared = False
    for key, value in infobox_fields(facts["infobox"]).items():
        if not re.fullmatch(r"(?i)games?(?:_?\d+)?|appearances?", key) or not value.strip():
            continue
        flags = {k.strip() for k, v in re.findall(r"\|\s*([^|=}]+?)\s*=\s*([^|}]*)", value) if v.strip().lower() in ("y", "yes", "1")}
        text = re.sub(r"\{\{[^{}]*\}\}", " ", value)
        if not flags and not text.strip():
            continue
        declared = True
        if flags & set(product_keys) or scope.matches(text):
            return True
    return False if declared else None


def is_location_bearing(facts: dict) -> bool:
    if facts["infobox"]:
        header = re.match(r"\{\{\s*infobox\s*([^|\n}]*)", facts["infobox"], re.I)
        if header and has_place_noun(header.group(1)):
            return True
        if has_place_noun(infobox_fields(facts["infobox"]).get("type", "")):
            return True
    return has_place_noun(lead_head_phrase(facts["lead"]))


class Source:
    def __init__(self, config: dict, user_agent: str, delay: float, budget: int) -> None:
        self.id = config["id"]
        self.api = config["api"]
        self.index = config["index"]
        self.user_agent = user_agent
        self.delay = delay
        self.budget = budget
        self.requests = 0
        self.stalled_continuations = 0
        self.product_game_keys = list(config.get("productGameTemplateKeys", []))

    def get(self, params: dict) -> dict:
        if self.requests >= self.budget:
            raise BudgetExhausted(self.id)
        self.requests += 1
        if self.requests % 100 == 0:
            print(f"  {self.id}: {self.requests} requests", flush=True)
        params = dict(params, format="json", formatversion="2")
        url = self.api + "?" + urllib.parse.urlencode(params)
        request = urllib.request.Request(url, headers={"User-Agent": self.user_agent})

        def fetch() -> dict:
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.loads(response.read())

        for attempt in range(5):
            # Socket timeouts do not bound name resolution or a stalled TLS read, so each attempt also has a
            # wall-clock deadline; an abandoned attempt's thread is left to die with the daemon pool.
            future = _POOL.submit(fetch)
            try:
                payload = future.result(timeout=120)
                break
            except Exception:  # noqa: BLE001 - transient network failure is retried then surfaced
                if attempt == 4:
                    raise
                time.sleep(2 + attempt * 3)
        time.sleep(self.delay)
        if "error" in payload:
            raise RuntimeError(f"{self.id}: {payload['error'].get('info')}")
        return payload

    def pages(self, titles: list[str]) -> dict:
        """Batch page fetch with full continuation; returns query maps and pages."""
        merged: dict = {"pages": {}, "redirects": [], "normalized": []}
        params = {
            "action": "query", "prop": "revisions|categories", "rvprop": "ids|content", "rvslots": "main",
            "cllimit": "max", "clshow": "!hidden", "redirects": "1", "titles": "|".join(titles),
        }
        cont: dict = {}
        seen: set[str] = set()
        while True:
            payload = self.get(dict(params, **cont))
            query = payload.get("query", {})
            merged["redirects"].extend(query.get("redirects", []))
            merged["normalized"].extend(query.get("normalized", []))
            for page in query.get("pages", []):
                key = page.get("title")
                existing = merged["pages"].setdefault(key, {"title": key})
                for k, v in page.items():
                    if k == "categories":
                        existing.setdefault("categories", []).extend(v)
                    elif k == "revisions":
                        if v and not existing.get("revisions"):
                            existing["revisions"] = v
                    else:
                        existing[k] = v
            if "continue" not in payload:
                break
            cont = self._advance(seen, payload["continue"], "pages")
            if cont is None:
                break
        if len(titles) > 1:
            unfetched = [p["title"] for p in merged["pages"].values()
                         if not p.get("revisions") and not p.get("missing") and not p.get("invalid")]
            for title in unfetched:
                single = self.pages([title])
                for key, page in single["pages"].items():
                    merged["pages"][key] = page
        return merged

    def _advance(self, seen: set[str], nxt: dict, what: str) -> dict | None:
        # Some wikis cycle continuation tokens indefinitely; a token already seen cannot yield new rows.
        key = json.dumps(nxt, sort_keys=True)
        if key in seen:
            self.stalled_continuations += 1
            print(f"  {self.id}: {what} continuation repeated {nxt}", flush=True)
            return None
        seen.add(key)
        return nxt

    def category_members(self, category: str) -> list[dict]:
        members = []
        cont: dict = {}
        seen: set[str] = set()
        while True:
            payload = self.get(dict({
                "action": "query", "list": "categorymembers", "cmtitle": category, "cmlimit": "500",
                "cmprop": "title|type|ns",
            }, **cont))
            members.extend(payload.get("query", {}).get("categorymembers", []))
            if "continue" not in payload:
                break
            cont = self._advance(seen, payload["continue"], "categorymembers " + category)
            if cont is None:
                break
        return members


class BudgetExhausted(Exception):
    pass


def slug(title: str) -> str:
    value = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")
    return (value or "page")[:80]


def native_seed_names(table: Path) -> list[str]:
    names = set()
    for line in table.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^\|\s*[0-9]+\s*\|\s*[^|]+\|\s*[A-Za-z0-9_]+\s*\|\s*([^|]+?)\s*\|\s*$", line)
        if not match:
            continue
        name = match.group(1).strip()
        names.add(name)
        # A combined native label "A / B" names each part as well.
        for part in name.split(" / "):
            if part.strip():
                names.add(part.strip())
    return sorted(names)


def crawl_source(source: Source, config: dict, scope: ScopeMatcher, seeds: list[str], out_dir: Path) -> dict:
    page_queue: list[tuple[str, str, bool]] = [(t, "seed:product-title", True) for t in config["productTitlePages"]]
    page_queue += [(t, "seed:native-zone-name", False) for t in seeds]
    category_queue: list[tuple[str, str]] = []
    examined_titles: dict[str, dict] = {}
    examined_categories: dict[str, dict] = {}
    resolved_title: dict[str, str] = {}
    pinned: dict[str, dict] = {}
    discovered_via: dict[str, set[str]] = {}
    redirect_aliases: dict[str, set[str]] = {}
    termination = "frontier-exhausted"
    pinned_titles_lower: set[str] = set()
    examined_final: set[str] = set()
    queued_pages: set[str] = {q[0] for q in page_queue}
    queued_categories: set[str] = set()

    def enqueue_page(title: str, via: str, product: bool = False) -> None:
        if not title:
            return
        discovered_via.setdefault(title, set()).add(via)
        if title in examined_titles or title in resolved_title:
            target = resolved_title.get(title)
            if target:
                discovered_via.setdefault(target, set()).add(via)
            return
        if title in queued_pages:
            return
        queued_pages.add(title)
        page_queue.append((title, via, product))

    def enqueue_category(title: str, via: str) -> None:
        if title in examined_categories or title in queued_categories:
            return
        queued_categories.add(title)
        category_queue.append((title, via))

    try:
        while page_queue or category_queue:
            if page_queue:
                batch = page_queue[:50]
                del page_queue[:50]
                before = source.requests
                result = source.pages([b[0] for b in batch])
                if source.requests - before > 3:
                    print(f"  {source.id}: page batch took {source.requests - before} requests", flush=True)
                mapping = {b[0]: b[0] for b in batch}
                for n in result["normalized"]:
                    for k, v in list(mapping.items()):
                        if v == n["from"]:
                            mapping[k] = n["to"]
                for r in result["redirects"]:
                    for k, v in list(mapping.items()):
                        if v == r["from"]:
                            mapping[k] = r["to"]
                            redirect_aliases.setdefault(r["to"], set()).add(r["from"])
                for requested, via, product in batch:
                    final = mapping[requested]
                    resolved_title[requested] = final
                    page = result["pages"].get(final)
                    entry = {"requested": requested, "title": final, "via": via}
                    if page is None or page.get("missing") or page.get("invalid") or not page.get("revisions"):
                        entry["status"] = "missing" if page is None or page.get("missing") or page.get("invalid") \
                            else "content-withheld-by-source"
                        examined_titles[requested] = entry
                        continue
                    if final in examined_final:
                        entry["status"] = "duplicate-of-examined"
                        examined_titles[requested] = entry
                        discovered_via.setdefault(final, set()).add(via)
                        continue
                    examined_final.add(final)
                    revision = page["revisions"][0]
                    wikitext = revision["slots"]["main"].get("content", "")
                    if re.match(r"(?i)^\s*#redirect", wikitext):
                        entry["status"] = "unresolved-redirect"
                        examined_titles[requested] = entry
                        continue
                    normalized, facts = extract(wikitext)
                    categories = sorted(set(c["title"].split(":", 1)[1] for c in page.get("categories", [])) | set(facts["categories"]))
                    facts["categories"] = categories
                    games = games_declaration(facts, scope, source.product_game_keys)
                    in_scope = product or games is True or any(scope.matches(c) for c in categories) or scope.matches(facts["lead"])
                    # A container reached only through another page is not admitted when its own page declares
                    # the games it appears in and the product is not among them.
                    transitive = via.startswith("container-of:") and games is not False
                    location = is_location_bearing(facts)
                    entry.update({"pageId": page.get("pageid"), "revid": revision["revid"], "inScope": in_scope,
                                  "locationBearing": location, "containerTransitive": transitive})
                    if product:
                        entry["status"] = "product-title-page"
                        examined_titles[requested] = entry
                        examined_titles.setdefault(final, entry)
                        for c in categories:
                            if scope.matches(c):
                                enqueue_category("Category:" + c, "category-of:" + final)
                        continue
                    if not location or not (in_scope or transitive):
                        entry["status"] = "examined-not-admitted"
                        entry["reason"] = ("not-location-bearing" if not location else
                                           "container-declared-for-other-games" if games is False and via.startswith("container-of:") else
                                           "out-of-product-scope")
                        examined_titles[requested] = entry
                        examined_titles.setdefault(final, entry)
                        continue
                    data = normalized.encode("utf-8")
                    file_name = f"{slug(final)}.{revision['revid']}.txt"
                    path = out_dir / source.id / file_name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                    pinned[final] = {
                        "source": source.id,
                        "title": final,
                        "pageId": page.get("pageid"),
                        "revid": revision["revid"],
                        "coordinate": source.index + "?" + urllib.parse.urlencode({"title": final.replace(" ", "_"), "oldid": revision["revid"]}),
                        "path": f"references/location-corpus/{source.id}/{file_name}",
                        "sha256": sha256(data),
                        "rawWikitextSha256": sha256(wikitext.encode("utf-8")),
                        "scope": "product" if in_scope else "container-transitive",
                    }
                    pinned_titles_lower.add(final.lower())
                    entry["status"] = "pinned"
                    examined_titles[requested] = entry
                    examined_titles.setdefault(final, entry)
                    # Containers named by infobox place fields and the lead are examined even when the
                    # container page carries no product marker: it contains an in-scope place.
                    container_text = facts["lead"]
                    if facts["infobox"]:
                        fields = infobox_fields(facts["infobox"])
                        container_text += "\n" + "\n".join(v for k, v in fields.items() if k in PLACE_FIELDS)
                    if in_scope:
                        for target in link_targets(container_text):
                            enqueue_page(target, "container-of:" + final)
                        for level, heading, body in facts["sections"]:
                            for target in link_targets(body):
                                enqueue_page(target, "member-list-of:" + final)
                        for c in categories:
                            if scope.matches(c) and has_place_noun(c) or c.lower() in pinned_titles_lower:
                                enqueue_category("Category:" + c, "category-of:" + final)
                continue
            category, via = category_queue.pop(0)
            name = category.split(":", 1)[1]
            before = source.requests
            members = source.category_members(category)
            print(f"  {source.id}: {category} members={len(members)} requests={source.requests - before} "
                  f"queue={len(page_queue)}/{len(category_queue)} pinned={len(pinned)}", flush=True)
            pages = [m["title"] for m in members if m.get("ns") == 0]
            subcats = [m["title"] for m in members if m.get("ns") == 14]
            examined_categories[category] = {"category": category, "via": via, "pages": len(pages), "subcategories": len(subcats)}
            for title in pages:
                enqueue_page(title, "category-member:" + name)
            for sub in subcats:
                sub_name = sub.split(":", 1)[1]
                if scope.matches(sub_name) and (has_place_noun(sub_name) or any(sub_name.lower().startswith(t) for t in pinned_titles_lower)):
                    enqueue_category(sub, "subcategory-of:" + name)
                else:
                    examined_categories.setdefault(sub, {"category": sub, "via": "subcategory-of:" + name,
                                                         "status": "not-followed", "reason": "not-a-scoped-place-category"})
    except BudgetExhausted:
        termination = "request-budget-exhausted"

    for title, entry in pinned.items():
        entry["discoveredVia"] = sorted(discovered_via.get(title, set()) | {
            v for requested, final in resolved_title.items() if final == title for v in discovered_via.get(requested, set())})
        entry["redirectAliases"] = sorted(redirect_aliases.get(title, set()))
    return {
        "source": source.id,
        "api": source.api,
        "requests": source.requests,
        "stalledContinuations": source.stalled_continuations,
        "termination": termination,
        "frontierRemaining": {"pages": len(page_queue), "categories": len(category_queue)},
        "pinned": [pinned[t] for t in sorted(pinned)],
        "examinedPages": [examined_titles[t] for t in sorted(examined_titles)],
        "examinedCategories": [examined_categories[c] for c in sorted(examined_categories)],
    }


def crawl(args: argparse.Namespace) -> None:
    config = json.loads(DISCOVERY.read_text(encoding="utf-8"))
    scope = ScopeMatcher(config["productScopeMarkers"])
    seeds = native_seed_names(Path(__file__).parent / config["nativeSeedTable"])
    CORPUS.mkdir(parents=True, exist_ok=True)
    results = []
    for source_config in config["sources"]:
        if args.source and source_config["id"] not in args.source:
            continue
        target = CORPUS / source_config["id"]
        if target.exists():
            for old in target.glob("*.txt"):
                old.unlink()
        source = Source(source_config, config["userAgent"], float(config["requestDelaySeconds"]), int(config["maxRequestsPerSource"]))
        print(f"crawling {source.id} ...", flush=True)
        result = crawl_source(source, config, scope, seeds, CORPUS)
        print(f"  {source.id}: pinned={len(result['pinned'])} examined={len(result['examinedPages'])} "
              f"categories={len(result['examinedCategories'])} requests={result['requests']} {result['termination']}", flush=True)
        results.append(result)
    acquired = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    ledger = {
        "schemaVersion": 1,
        "kind": "grid.location-registration.reference-discovery-ledger",
        "gameId": config["gameId"],
        "acquiredAtUtc": acquired,
        "discoveryConfigSha256": sha256(DISCOVERY.read_bytes()),
        "nativeSeedNames": seeds,
        "sources": [{k: v for k, v in r.items() if k != "pinned"} for r in results],
    }
    ledger_bytes = (json.dumps(ledger, ensure_ascii=False, indent=1, sort_keys=True) + "\n").encode("utf-8")
    ledger_path = CORPUS / "discovery-ledger.v1.json"
    ledger_path.write_bytes(ledger_bytes)
    native = json.loads(MANIFEST.read_text(encoding="utf-8"))["nativeTable"]
    manifest = {
        "schemaVersion": 3,
        "gameId": config["gameId"],
        "build": json.loads(MANIFEST.read_text(encoding="utf-8"))["build"],
        "locale": config["locale"],
        "acquiredAtUtc": acquired,
        "scope": "every Location-bearing reference page reached by exhaustive scoped discovery; relationships come only from generic grammars applied at registration",
        "nativeTable": native,
        "discoveryLedger": {"path": "references/location-corpus/discovery-ledger.v1.json", "sha256": sha256(ledger_bytes)},
        "productScopeMarkers": config["productScopeMarkers"],
        "sources": [p for r in results for p in r["pinned"]],
    }
    MANIFEST_V3.write_text(json.dumps(manifest, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    print(f"manifest v3: {len(manifest['sources'])} pinned pages", flush=True)


def verify_v3() -> None:
    manifest = json.loads(MANIFEST_V3.read_text(encoding="utf-8"))
    for source in manifest["sources"]:
        verify(Path(__file__).parent / source["path"], source["sha256"])
    verify(Path(__file__).parent / manifest["discoveryLedger"]["path"], manifest["discoveryLedger"]["sha256"])
    print(f"OK: {len(manifest['sources'])} pinned v3 Location pages match manifest v3.")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--verify-only", action="store_true")
    sub = parser.add_subparsers(dest="command")
    crawl_parser = sub.add_parser("crawl", help="exhaustive scoped Location reference discovery and pinning")
    crawl_parser.add_argument("--source", action="append", help="limit to a configured source id")
    sub.add_parser("verify-v3", help="verify pinned v3 corpus digests")
    args = parser.parse_args()
    if args.command == "crawl":
        crawl(args)
        return
    if args.command == "verify-v3":
        verify_v3()
        return
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if args.verify_only:
        for source in manifest["sources"]:
            verify(REFERENCES / Path(source["path"]).name, source["sha256"])
        verify(REFERENCES / Path(manifest["joinIndex"]["path"]).name, manifest["joinIndex"]["sha256"])
        verify(REFERENCES / Path(manifest["nativeTable"]["path"]).name, manifest["nativeTable"]["sha256"])
        print("OK: all pinned location hierarchy digests match manifest v2.")
        return
    print("Use 'crawl' for scoped v3 discovery; --verify-only checks pinned v2 digests; 'verify-v3' checks v3.")


if __name__ == "__main__":
    main()
