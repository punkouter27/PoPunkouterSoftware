#!/usr/bin/env python3
"""
Rebuild wwwroot/fonts/material-symbols-subset.woff2 from the full Material Symbols variable
font that ships inside the Radzen.Blazor NuGet package.

WHY THIS EXISTS
---------------
Radzen self-hosts MaterialSymbolsOutlined.woff2 — 1,068,920 bytes, roughly 3,600 glyphs — and
modern-ui.css declared it as family "Material Symbols" with `font-display: block`. This app
draws about thirty of those glyphs. So every cold load spent a megabyte on the critical path,
and `block` hides text for up to three seconds while it arrives, which is precisely the
blank-icon behaviour the comment above that @font-face criticises the old Google Fonts <link>
for. Subsetting removes the megabyte; the display strategy then stops mattering.

HOW THE NAMES ARE COLLECTED
---------------------------
Material Symbols renders through LIGATURES: the element's text is the icon's name and the font
substitutes a glyph for the letter run. So a name that is not in ICON_NAMES below does not
degrade to a blank box — it renders as the literal word "refresh" in the middle of a button.
That makes this list load-bearing, and it is why it is deliberately over-inclusive:

  * APP_ICONS — every name this repo names, including the dynamic ones. `grep -rho 'Icon="'`
    misses `Icon="@(_advancedOpen ? "expand_less" : "tune")"` and the whole switch in
    AzureWhatChanged.IconFor, so grep alone is not enough; they were also confirmed by
    walking both routes in a real browser with every disclosure forced open.
  * RADZEN_ICONS — glyphs Radzen's own components emit from inside the compiled assembly,
    where nothing in this repo can see them. Cheap insurance: each extra glyph costs a few
    hundred bytes against a megabyte saved.

ADDING AN ICON
--------------
Add the name to APP_ICONS and re-run:  python SCRIPTS/Build-IconFontSubset.py
Requires: pip install "fonttools[woff]" brotli

The script verifies every requested ligature actually survived into the subset and fails
loudly if one did not, so a typo cannot ship as a word rendered in a button.
"""

import re
import os
import sys

from fontTools.ttLib import TTFont
from fontTools.subset import Subsetter, Options

# Names this repository asks for by name.
APP_ICONS = [
    # MainLayout header (bare <span class="material-symbols-outlined">) and js/helpers.js,
    # which swaps textContent at runtime.
    "home", "menu", "close", "monitor_heart", "group",
    # RadzenIcon / RadzenButton Icon="..." across the client.
    "auto_awesome", "check_circle", "cloud_sync", "content_copy",
    "data_object", "delete_sweep", "download", "graphic_eq", "open_in_new",
    "history", "refresh", "security", "speed", "sync",
    "timeline", "troubleshoot",
    # PortfolioAppCard's "Preview pending" placeholder.
    "image",
    # Dynamic: AzureDashboard's advanced-diagnostics toggle.
    "expand_less", "tune",
    # Dynamic: AzureWhatChanged.IconFor.
    "cloud_off", "cloud_done", "trending_up", "trending_down", "shield", "sync_alt",
    # js/command-palette.js (set as textContent at runtime): its search glyph, the settings
    # entry, and the per-app launcher rows. "settings" is also the MainLayout header link.
    "search", "settings", "my_location", "link",
    # AzurePriorityQueue restart/start, AzureUptimeHeatmap error and alert chips,
    # PortfolioAppCard streak chip.
    "restart_alt", "play_arrow", "bug_report", "local_fire_department",
]

# Emitted by Radzen.Blazor's own components. Not greppable from this repo — they live in the
# compiled assembly — so this list is deliberately generous.
RADZEN_ICONS = [
    "arrow_drop_down", "arrow_drop_up", "expand_more", "chevron_left", "chevron_right",
    "first_page", "last_page", "keyboard_arrow_left", "keyboard_arrow_right",
    "keyboard_arrow_up", "keyboard_arrow_down", "arrow_upward", "arrow_downward",
    "filter_alt", "search", "more_vert", "more_horiz", "check", "done", "clear", "cancel",
    "add", "remove", "edit", "delete", "save", "settings", "info", "warning", "error",
    "error_outline", "visibility", "visibility_off", "star", "star_border", "event",
    "schedule", "today", "check_box", "check_box_outline_blank", "indeterminate_check_box",
    "radio_button_checked", "radio_button_unchecked", "notifications", "help",
    # RadzenAlert's Warning icon (every ErrorRetryBanner). Missing, it rendered the "warning"
    # glyph followed by the literal text "_amber".
    "warning_amber",
]

ICON_NAMES = sorted(set(APP_ICONS) | set(RADZEN_ICONS))

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "src", "PoPunkouterSoftware.Client", "wwwroot", "fonts",
                   "material-symbols-subset.woff2")


def find_source_font() -> str:
    """MaterialSymbolsOutlined.woff2 from the Radzen.Blazor version this repo pins.

    Not "the newest in the cache": the cache holds other repos' versions too, and a string
    sort ranked 8.7.3 above 10.4.5, so the subset silently came from a stale font."""
    props = open(os.path.join(REPO, "Directory.Packages.props"), encoding="utf-8").read()
    pinned = re.search(r'Include="Radzen\.Blazor"\s+Version="([^"]+)"', props)
    if not pinned:
        sys.exit("Radzen.Blazor is not pinned in Directory.Packages.props")
    path = os.path.join(
        os.path.expanduser("~"), ".nuget", "packages", "radzen.blazor", pinned.group(1),
        "staticwebassets", "fonts", "MaterialSymbolsOutlined.woff2")
    if not os.path.exists(path):
        sys.exit(f"Source font not found. Restore the solution first.\nLooked in: {path}")
    return path


def ligature_names(font: TTFont) -> set:
    """Every ligature in the font, spelled back out as the text that triggers it."""
    reverse = {glyph: ch for ch, glyph in font.getBestCmap().items()}
    found = set()
    gsub = font.get("GSUB")
    if gsub is None:
        return found
    for lookup in gsub.table.LookupList.Lookup:
        for sub in lookup.SubTable:
            # Material Symbols stores its ligatures in LookupType 7 (Extension) subtables,
            # so the real LigatureSubst is one level down. Reading `sub.ligatures` directly
            # finds nothing and reports every single icon as missing.
            sub = getattr(sub, "ExtSubTable", sub)
            ligatures = getattr(sub, "ligatures", None)
            if not ligatures:
                continue
            for first, entries in ligatures.items():
                for lig in entries:
                    glyphs = [first] + list(lig.Component)
                    try:
                        found.add("".join(chr(reverse[g]) for g in glyphs))
                    except KeyError:
                        pass  # a component with no codepoint cannot be typed anyway
    return found


def prune_ligatures(font: TTFont, keep: set) -> int:
    """
    Delete every ligature whose spelled-out name is not in `keep`, and report how many were
    dropped.

    This step is what actually makes the subset small, and it has to happen BEFORE
    `Subsetter.subset`. Every one of the font's ~3,600 icon names is spelled with the same
    twenty-six letters, so `--text` alone retains the letters, the letters keep every ligature
    reachable from them, and the closure therefore keeps essentially the whole font: measured
    at 958,864 bytes, an 11% saving on a megabyte. Removing the substitutions first leaves the
    closure nothing to reach, and the glyphs go with them.
    """
    reverse = {glyph: ch for ch, glyph in font.getBestCmap().items()}
    dropped = 0
    for lookup in font["GSUB"].table.LookupList.Lookup:
        for sub in lookup.SubTable:
            sub = getattr(sub, "ExtSubTable", sub)
            ligatures = getattr(sub, "ligatures", None)
            if not ligatures:
                continue
            for first in list(ligatures):
                kept = []
                for lig in ligatures[first]:
                    try:
                        name = "".join(chr(reverse[g]) for g in [first] + list(lig.Component))
                    except KeyError:
                        name = None
                    if name in keep:
                        kept.append(lig)
                    else:
                        dropped += 1
                if kept:
                    ligatures[first] = kept
                else:
                    del ligatures[first]
    return dropped


def main() -> None:
    source = find_source_font()
    before = os.path.getsize(source)

    font = TTFont(source)
    dropped = prune_ligatures(font, set(ICON_NAMES))
    options = Options()
    options.flavor = "woff2"
    # Material Symbols does NOT use `liga`: its two features are `rlig` and `rclt` (verified
    # by reading the FeatureList of the shipped font). Both are in fontTools' default retained
    # set, but they are named explicitly here because dropping either turns every icon in the
    # app into the literal word it is named after.
    options.layout_features = sorted(set(options.layout_features) | {"rlig", "rclt", "liga", "dlig", "ccmp"})
    # Keep the variable axes: modern-ui.css declares `font-weight: 100 700` against this file.
    options.recalc_bounds = True
    options.name_IDs = ["*"]
    options.notdef_outline = True

    subsetter = Subsetter(options=options)
    # Ligature closure keeps a ligature whose components all survive, so passing the names as
    # text retains both the letters and the substitutions that turn them into glyphs.
    subsetter.populate(text=" ".join(ICON_NAMES))
    subsetter.subset(font)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    font.save(OUT)
    font.close()

    # Verify, rather than trust: a name that lost its ligature renders as a WORD in a button,
    # which is the one failure mode of this optimisation and it is silent.
    check = TTFont(OUT)
    have = ligature_names(check)
    check.close()
    missing = [n for n in ICON_NAMES if n not in have]
    if missing:
        sys.exit("Ligature missing from the subset for: " + ", ".join(missing))

    after = os.path.getsize(OUT)
    print(f"source : {source}")
    print(f"output : {OUT}")
    print(f"glyph names: {len(ICON_NAMES)}   all ligatures verified present "
          f"({dropped:,} others pruned)")
    print(f"size   : {before:,} -> {after:,} bytes "
          f"({100 - after * 100 // before}% smaller)")


if __name__ == "__main__":
    main()
