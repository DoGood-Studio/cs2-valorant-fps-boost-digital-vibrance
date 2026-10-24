"""
prosettings.net'teki top takım oyuncularının ayarlarını Data/ProSettings.json dosyasına yazar.

Kullanım:  python tools/update_prosettings.py
Kaynak listeler: https://prosettings.net/lists/cs2/ ve https://prosettings.net/lists/valorant/
Her oyuncunun sayfası ayrıca okunur (crosshair, viewmodel, görüntü ayarları, son güncelleme tarihi, fotoğraf adresi).
Fotoğraf ve logolar indirilmez; yalnızca adresleri yazılır (uygulama çalışırken indirip önbelleğe alır).
Siteyi yormamak için en fazla 4 eşzamanlı istek yapılır.
"""
import concurrent.futures as cf
import datetime
import html
import json
import os
import re
import sys
import time
import urllib.request

UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GameColorGuard-data/1.0 (+github)"}
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Data", "ProSettings.json")
GAMES = {"cs2": "CS2", "valorant": "VALORANT"}
# Top takım listesinde olmasa da korunacak oyuncular (oyun, slug)
EXTRAS = [("cs2", "s1mple"), ("valorant", "cned")]


def get(url, tries=3):
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=40) as r:
                return r.read().decode("utf-8", "replace")
        except Exception:
            if attempt == tries - 1:
                raise
            time.sleep(2 + attempt * 3)


def text(fragment):
    return html.unescape(re.sub(r"<[^>]+>", " ", fragment)).replace("\xa0", " ").strip()


def squash(value):
    return re.sub(r"\s+", " ", value).strip()


def num(value):
    if value is None:
        return None
    m = re.search(r"-?\d+(?:\.\d+)?", value.replace(",", "."))
    if not m:
        return None
    f = float(m.group(0))
    return int(f) if f.is_integer() else f


def yes(value):
    if value is None:
        return None
    v = value.strip().lower()
    if v in ("yes", "on", "true", "enabled"):
        return True
    if v in ("no", "off", "false", "disabled"):
        return False
    return None


def parse_list(game):
    page = get(f"https://prosettings.net/lists/{game}/")
    headers = [squash(text(h)) for h in re.findall(r"<th[^>]*>(.*?)</th>", page, re.S)]
    body = page[page.find("<tbody"):] if "<tbody" in page else page
    rows = []
    for row in re.findall(r"<tr[^>]*>(.*?)</tr>", body, re.S):
        cells = re.findall(r"<td[^>]*>(.*?)</td>", row, re.S)
        if len(cells) < 6:
            continue
        cell = dict(zip(headers, cells))
        team_html = cell.get("Team", "")
        player_html = cell.get("Player", "")
        slug = re.search(r"/players/([^/\"]+)/", player_html)
        if not slug:
            continue
        team_slug = re.search(r"/teams/([^/\"]+)/", team_html)
        logo = re.search(r'src="([^"]+?)-20x20\.(png|jpe?g|webp)"', team_html)
        svg_logo = re.search(r'src="([^"]+?\.svg)"', team_html) if not logo else None
        flag = re.search(r"/flags/[^\"]*/([a-z]{2})\.svg\"\s+alt=\"([^\"]*)\"", player_html)
        rows.append({
            "game": game,
            "slug": slug.group(1),
            "name": squash(text(player_html)),
            "team": squash(text(team_html)) or None,
            "teamSlug": team_slug.group(1) if team_slug else None,
            # Logo: raster ise 40 px (2x) sürümü, yalnız SVG varsa SVG (uygulama düz şekilli SVG'leri çizebilir).
            "teamLogoUrl": f"{logo.group(1)}-20x20-2x.{logo.group(2)}" if logo else (svg_logo.group(1) if svg_logo else None),
            "country": flag.group(1).upper() if flag else None,
            "countryName": html.unescape(flag.group(2)) if flag else None,
            "role": squash(text(cell.get("Role", ""))) or None,
            "mouseModel": squash(text(cell.get("Mouse", ""))) or None,
        })
    return rows


def section(page, game):
    """Çok oyunlu sayfalarda yalnız istenen oyunun bölümü (son 'X Settings' başlığından bir sonrakine)."""
    label = GAMES[game]
    heads = [(m.start(), squash(text(m.group(1)))) for m in re.finditer(r"<h2[^>]*>(.*?)</h2>", page, re.S)]
    starts = [pos for pos, t in heads if t == f"{label} Settings"]
    if not starts:
        return page
    start = starts[-1]
    later = [pos for pos, t in heads if pos > start and t.endswith(" Settings") and t != f"{label} Settings"]
    return page[start:later[0] if later else len(page)]


def pairs(fragment):
    found = {}
    for k, v in re.findall(r"<th[^>]*>(.*?)</th>\s*<td[^>]*>(.*?)</td>", fragment, re.S):
        key = squash(text(k))
        if key not in found:  # ilk değer (Valorant'ta bazı etiketler iç/dış çizgide tekrar eder)
            found[key] = squash(text(v))
    return found


def launch_options(fragment):
    m = re.search(r"<h[23][^>]*>\s*Launch Options\s*</h[23]>(.*?)<h[23]", fragment, re.S)
    if not m:
        return None
    value = squash(text(m.group(1)))
    return None if not value or "does not use" in value.lower() else value


def parse_player(entry):
    game, slug = entry["game"], entry["slug"]
    url = f"https://prosettings.net/players/{slug}/"
    page = get(url)
    sec = section(page, game)
    p = pairs(sec)
    overview = pairs(page)  # Ad, Ülke ve Takım sayfanın başındaki tabloda; pairs() ilk değeri tutar

    updated = re.search(r"Last updated:\s*(?:<[^>]+>\s*)*([A-Z][a-z]{2} \d{1,2}, \d{4})", page)
    photo = re.search(r"(https://prosettings\.net/wp-content/uploads/[^\"\s,]+?-220x220-2x-[^\"\s,]+?\.(?:webp|png|jpe?g))", page)
    name_tag = re.search(r"<h1[^>]*>(.*?)</h1>", page, re.S)

    player = {
        "id": slug,
        "name": entry.get("name") or (squash(text(name_tag.group(1))) if name_tag else slug),
        "realName": overview.get("Name"),
        "team": entry.get("team") or overview.get("Team"),
        "teamSlug": entry.get("teamSlug"),
        "teamLogoUrl": entry.get("teamLogoUrl"),
        "country": entry.get("country"),
        "countryName": entry.get("countryName") or overview.get("Country"),
        "role": entry.get("role"),
        "game": game,
        "url": url,
        "photoUrl": photo.group(1) if photo else None,
        "lastUpdated": datetime.datetime.strptime(updated.group(1), "%b %d, %Y").strftime("%Y-%m-%d") if updated else None,
        "mouse": {
            "model": entry.get("mouseModel"),
            "dpi": num(p.get("DPI")),
            "sensitivity": num(p.get("Sensitivity")),
            "edpi": num(p.get("eDPI")),
            "zoomSensitivity": num(p.get("Zoom Sensitivity")),
            "scopedSensitivity": num(p.get("Scoped Sensitivity")),
            "hz": num(p.get("Hz")),
            "windowsSensitivity": num(p.get("Windows Sensitivity")),
        },
        "video": {
            "resolution": p.get("Resolution"),
            "aspectRatio": p.get("Aspect Ratio"),
            "scalingMode": p.get("Scaling Mode") or p.get("Aspect Ratio Method"),
            "displayMode": p.get("Display Mode"),
            "brightness": p.get("Brightness"),
        },
        "launchOptions": launch_options(sec) if game == "cs2" else None,
    }
    if game == "cs2":
        outline = yes(p.get("Outline"))
        player["crosshair"] = {
            "code": None,
            "style": p.get("Style"),
            "length": num(p.get("Length (px)")),
            "thickness": num(p.get("Thickness (px)")),
            "gap": num(p.get("Gap (px)")),
            "outline": outline,
            "outlineThickness": num(p.get("Outline Thickness")) if outline else None,
            "colorR": num(p.get("Red")),
            "colorG": num(p.get("Green")),
            "colorB": num(p.get("Blue")),
            "alpha": num(p.get("Opacity")),
            "dot": yes(p.get("Dot")),
            "tStyle": yes(p.get("T Style")),
            "followRecoil": yes(p.get("Follow Recoil")),
        }
        player["viewmodel"] = {
            "fov": num(p.get("FOV")),
            "offsetX": num(p.get("Offset X")),
            "offsetY": num(p.get("Offset Y")),
            "offsetZ": num(p.get("Offset Z")),
            "presetPos": None,
        }
    else:
        code = p.get("Code")
        player["crosshair"] = {"code": code if code and code.startswith("0;") else None}
    return player


def main():
    entries = []
    for game in GAMES:
        rows = parse_list(game)
        print(f"{game}: listede {len(rows)} oyuncu")
        entries += rows
    known = {(e["game"], e["slug"]) for e in entries}
    for game, slug in EXTRAS:
        if (game, slug) not in known:
            entries.append({"game": game, "slug": slug})

    players, failed = [], []
    with cf.ThreadPoolExecutor(max_workers=4) as pool:
        futures = {pool.submit(parse_player, e): e for e in entries}
        for future in cf.as_completed(futures):
            e = futures[future]
            try:
                players.append(future.result())
            except Exception as ex:
                failed.append(f"{e['game']}/{e['slug']}: {ex}")

    team_order = {}
    for e in entries:
        team_order.setdefault((e["game"], e.get("teamSlug")), len(team_order))
    players.sort(key=lambda p: (list(GAMES).index(p["game"]), team_order.get((p["game"], p["teamSlug"]), 999), p["name"].lower()))
    teams = {}
    for p in players:
        if p["teamSlug"]:
            t = teams.setdefault((p["game"], p["teamSlug"]), {"game": p["game"], "slug": p["teamSlug"], "name": p["team"], "logoUrl": p["teamLogoUrl"]})
    data = {
        "source": "prosettings.net",
        "fetchedAt": datetime.date.today().isoformat(),
        "notes": failed,
        "teams": list(teams.values()),
        "players": players,
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print(f"yazıldı: {OUT} — {len(players)} oyuncu, {len(teams)} takım, {len(failed)} hata")
    for line in failed:
        print("  HATA", line)


if __name__ == "__main__":
    sys.exit(main())
