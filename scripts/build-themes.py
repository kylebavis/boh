"""
Derives boh's theme CSS from upstream editor palettes.

Upstream schemes are designed for syntax highlighting on one background, not for web
chrome, so almost none of them clear WCAG AA for every pair boh actually renders. This
script takes the faithful palette, then nudges only the tokens that fail — moving
lightness in HSL, which preserves hue and saturation so the scheme stays recognisable —
and emits CSS once everything passes.
"""
import colorsys
import pathlib


# Each palette maps the 15-token contract. Values are taken from the upstream
# scheme wherever the scheme defines a suitable tone; derived tones are marked.
PALETTES = {
    "nord": dict(  # arcticicestudio/nord, MIT
        mode="dark",
        bg="#2e3440",          # nord0
        surface="#3b4252",     # nord1
        surface2="#434c5e",    # nord2
        overlay="#3b4252",     # nord1
        fg="#d8dee9",          # nord4
        fg_strong="#eceff4",   # nord6
        fg_muted="#a3adbf",    # derived: nord3 lifted for body-text contrast
        border="#4c566a",      # nord3
        border_muted="#3b4252",# nord1
        accent="#88c0d0",      # nord8
        accent_fg="#2e3440",   # nord0
        danger="#bf616a",      # nord11
        success="#a3be8c",     # nord14
        warning="#ebcb8b",     # nord13
        warning_fg="#2e3440",
    ),
    "dracula": dict(  # dracula/visual-studio-code, MIT
        mode="dark",
        bg="#282a36",
        surface="#343746",     # BGLight
        surface2="#424450",    # BGLighter
        overlay="#44475a",     # SELECTION
        fg="#f8f8f2",
        fg_strong="#ffffff",
        fg_muted="#a9aecd",    # derived: COMMENT #6272a4 lifted for contrast
        border="#44475a",
        border_muted="#343746",
        accent="#bd93f9",      # PURPLE
        accent_fg="#282a36",
        danger="#ff5555",
        success="#50fa7b",
        warning="#f1fa8c",
        warning_fg="#282a36",
    ),
    "monokai": dict(  # microsoft/vscode theme-monokai, MIT
        mode="dark",
        bg="#272822",
        surface="#32332c",     # derived lift of bg
        surface2="#414339",    # input.background
        overlay="#414339",
        fg="#f8f8f2",
        fg_strong="#ffffff",
        fg_muted="#b0aa93",    # derived: comment #75715e lifted for contrast
        border="#75715e",
        border_muted="#414339",
        accent="#66d9ef",
        accent_fg="#272822",
        danger="#f92672",
        success="#a6e22e",
        warning="#e6db74",
        warning_fg="#272822",
    ),
    "gruvbox-dark": dict(  # morhetz/gruvbox, MIT
        mode="dark",
        bg="#282828",          # bg0
        surface="#32302f",     # bg0_s
        surface2="#3c3836",    # bg1
        overlay="#504945",     # bg2
        fg="#ebdbb2",          # fg1
        fg_strong="#fbf1c7",   # fg0
        fg_muted="#bdae93",    # fg3
        border="#665c54",      # bg3
        border_muted="#3c3836",# bg1
        accent="#83a598",      # bright blue
        accent_fg="#282828",
        danger="#fb4934",
        success="#b8bb26",
        warning="#fabd2f",
        warning_fg="#282828",
    ),
    "catppuccin-mocha": dict(  # catppuccin/palette, MIT
        mode="dark",
        bg="#1e1e2e",          # base
        surface="#313244",     # surface0
        surface2="#45475a",    # surface1
        overlay="#313244",     # surface0
        fg="#cdd6f4",          # text
        fg_strong="#cdd6f4",   # text (palette has no brighter tone)
        fg_muted="#a6adc8",    # subtext0
        border="#585b70",      # surface2
        border_muted="#45475a",# surface1
        accent="#89b4fa",      # blue
        accent_fg="#1e1e2e",
        danger="#f38ba8",      # red
        success="#a6e3a1",     # green
        warning="#f9e2af",     # yellow
        warning_fg="#1e1e2e",
    ),
    "solarized-dark": dict(  # altercation/solarized, MIT
        mode="dark",
        bg="#002b36",          # base03
        surface="#073642",     # base02
        surface2="#0e4b59",    # derived lift of base02
        overlay="#073642",     # base02
        # Same ordering problem as Solarized Light, mirrored: base0 as muted text has to be
        # lifted to clear surface2 and overtakes base1 body text. Moving body text up to
        # base2 puts the ramp back in order.
        fg="#eee8d5",          # base2
        fg_strong="#fdf6e3",   # base3
        fg_muted="#93a1a1",    # base1
        border="#586e75",      # base01
        border_muted="#073642",# base02
        accent="#268bd2",      # blue
        accent_fg="#002b36",
        danger="#dc322f",
        success="#859900",
        warning="#b58900",
        warning_fg="#fdf6e3",
    ),
    "gruvbox-light": dict(  # morhetz/gruvbox, MIT
        mode="light",
        bg="#f9f5d7",          # bg0_h (hard light)
        surface="#fbf1c7",     # bg0
        surface2="#ebdbb2",    # bg1
        overlay="#ffffff",     # derived: inputs sit above the page in light mode
        fg="#3c3836",          # fg1
        fg_strong="#282828",   # fg0
        fg_muted="#7c6f64",    # fg4
        border="#bdae93",      # bg3
        border_muted="#d5c4a1",# bg2
        accent="#076678",      # neutral blue
        accent_fg="#fbf1c7",
        danger="#9d0006",
        success="#79740e",
        warning="#fabd2f",
        warning_fg="#3c3836",
    ),
    "catppuccin-latte": dict(  # catppuccin/palette, MIT
        mode="light",
        bg="#e6e9ef",          # mantle — page sits below the cards
        surface="#eff1f5",     # base
        surface2="#dce0e8",    # crust
        overlay="#ffffff",     # derived
        fg="#4c4f69",          # text
        fg_strong="#4c4f69",   # text (palette has no darker tone)
        fg_muted="#6c6f85",    # subtext0
        border="#acb0be",      # surface2
        border_muted="#ccd0da",# surface0
        accent="#1e66f5",      # blue
        accent_fg="#eff1f5",
        danger="#d20f39",      # red
        success="#40a02b",     # green
        warning="#df8e1d",     # yellow
        warning_fg="#eff1f5",
    ),
    "solarized-light": dict(  # altercation/solarized, MIT
        mode="light",
        bg="#eee8d5",          # base2 — page below the cards
        surface="#fdf6e3",     # base3
        surface2="#e4ddc9",    # derived shade of base2
        overlay="#fdf6e3",     # base3
        # Solarized's own body tone for light backgrounds is base00, but muted text has to
        # clear AA against surface2 as well, which drags it darker than base01 would be —
        # leaving "muted" more prominent than body text. Taking body text a step down the
        # ramp to base02 restores the ordering with room to spare.
        fg="#073642",          # base02
        fg_strong="#002b36",   # base03
        fg_muted="#657b83",    # base00
        border="#93a1a1",      # base1
        border_muted="#d9d2bd",# derived
        accent="#268bd2",      # blue
        accent_fg="#fdf6e3",
        danger="#dc322f",
        success="#859900",
        warning="#b58900",
        warning_fg="#fdf6e3",
    ),
}


def srgb(c):
    c = c / 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def lum(hex_color):
    h = hex_color.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
    return 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b)


def ratio(a, b):
    la, lb = lum(a), lum(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


# (foreground token, background token, minimum ratio, what it renders)
TEXT_CHECKS = [
    ("fg", "bg", 4.5, "body text on page"),
    ("fg", "surface", 4.5, "body text on card"),
    ("fg", "overlay", 4.5, "input text"),
    ("fg", "surface2", 4.5, "label on secondary button"),
    ("fg_muted", "overlay", 4.5, "input placeholder"),
    ("fg_strong", "bg", 4.5, "headings"),
    ("fg_strong", "surface", 4.5, "headings on card"),
    ("fg_muted", "bg", 4.5, "muted text on page"),
    ("fg_muted", "surface", 4.5, "muted text on card"),
    ("fg_muted", "surface2", 4.5, "tag counts on chips"),
    ("accent", "bg", 4.5, "links on page"),
    ("accent", "surface", 4.5, "links on card"),
    ("accent", "surface2", 4.5, "links on chips"),
    ("danger", "bg", 4.5, "error text"),
    ("danger", "surface", 4.5, "error text on card"),
    # Only 3.0: this one is the "x" glyph on a tag chip's hover state, and holding it to
    # 4.5 against the lightest surface washed every red in the set out to pink.
    ("danger", "surface2", 3.0, "tag remove hover"),
    ("success", "bg", 4.5, "ins text"),
]

# Non-text boundaries. Lower bars: these are dividers and outlines, not glyphs.
BORDER_CHECKS = [
    ("border", "bg", 2.0, "input outline on page"),
    ("border", "surface", 2.0, "input outline on card"),
    ("border_muted", "bg", 1.25, "divider on page"),
    ("border_muted", "surface", 1.25, "divider on card"),
]

# Tokens that render *as* a background with text on top: pick the best partner rather
# than nudging, since these are fills whose whole job is to carry a label.
INVERSE_PAIRS = [("accent_fg", "accent"), ("warning_fg", "warning")]

# Saturation is only bought back for colours that are actually chromatic. Across these
# nine schemes the greys sit at or below 0.18 and every accent at or above 0.42, so the
# split is unambiguous — without it, lifting Dracula's #44475a border turned it purple.
CHROMATIC = 0.30


def to_hsl(h):
    h = h.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    hh, ll, ss = colorsys.rgb_to_hls(r, g, b)
    return hh, ll, ss


def to_hex(hh, ll, ss):
    r, g, b = colorsys.hls_to_rgb(hh, max(0.0, min(1.0, ll)), ss)
    return "#{:02x}{:02x}{:02x}".format(*(round(c * 255) for c in (r, g, b)))


def nudge(color, backgrounds, need, lighten):
    """
    Walk lightness until `color` clears `need` against every background.

    Saturation rises with the distance travelled. Moving lightness alone bleeds chroma —
    lifting Nord's #bf616a far enough to make error text legible on nord0 turned it into a
    pale pink — so each step of lightness buys back an equal step of saturation, which
    keeps a red reading as red at the contrast the text actually needs.
    """
    hh, l0, s0 = to_hsl(color)
    ll = l0
    step = 0.005 if lighten else -0.005
    for _ in range(200):
        compensated = min(1.0, s0 + abs(ll - l0)) if s0 > CHROMATIC else s0
        candidate = to_hex(hh, ll, compensated)
        if all(ratio(candidate, bg) >= need for bg in backgrounds):
            return candidate
        ll += step
        if not 0.0 <= ll <= 1.0:
            break
    return to_hex(hh, ll, min(1.0, s0 + abs(ll - l0)) if s0 > CHROMATIC else s0)


def best_inverse(fill, palette):
    """
    Choose a label colour for a filled swatch. Ordered by preference, not by contrast:
    the scheme's own background is the intended label colour on an accent fill, so take
    the first option that clears AA and only escalate to plain black/white if none do.
    """
    options = [palette["bg"], palette["surface"], palette["fg_strong"], "#000000", "#ffffff"]
    for o in options:
        if ratio(o, fill) >= 4.5:
            return o
    return max((ratio(o, fill), o) for o in options)[1]


def adjust(name, palette):
    p = dict(palette)
    lighten = p["mode"] == "dark"
    moved = {}

    # Text tokens: gather every background each one must clear, then nudge once.
    by_token = {}
    for fg, bg, need, _ in TEXT_CHECKS:
        by_token.setdefault(fg, {"bgs": [], "need": need})
        by_token[fg]["bgs"].append(p[bg])
        by_token[fg]["need"] = max(by_token[fg]["need"], need)

    for token, spec in by_token.items():
        fixed = nudge(p[token], spec["bgs"], spec["need"], lighten)
        if fixed != p[token]:
            moved[token] = (p[token], fixed)
            p[token] = fixed

    # Borders move the same way but against a far lower bar.
    for token in ("border", "border_muted"):
        bgs = [p[bg] for tok, bg, _, _ in BORDER_CHECKS if tok == token]
        need = max(n for tok, _, n, _ in BORDER_CHECKS if tok == token)
        fixed = nudge(p[token], bgs, need, lighten)
        if fixed != p[token]:
            moved[token] = (p[token], fixed)
            p[token] = fixed

    # Labels on filled swatches are chosen, not nudged.
    for label, fill in INVERSE_PAIRS:
        best = best_inverse(p[fill], p)
        if best != p[label]:
            moved[label] = (p[label], best)
            p[label] = best

    return p, moved


TOKENS = [
    ("bg", "--boh-bg"), ("surface", "--boh-surface"), ("surface2", "--boh-surface-2"),
    ("overlay", "--boh-overlay"), ("fg", "--boh-fg"), ("fg_strong", "--boh-fg-strong"),
    ("fg_muted", "--boh-fg-muted"), ("border", "--boh-border"),
    ("border_muted", "--boh-border-muted"), ("accent", "--boh-accent"),
    ("accent_fg", "--boh-accent-fg"), ("danger", "--boh-danger"),
    ("success", "--boh-success"), ("warning", "--boh-warning"),
    ("warning_fg", "--boh-warning-fg"),
]

LABELS = {
    "nord": "Nord", "dracula": "Dracula", "monokai": "Monokai",
    "gruvbox-dark": "Gruvbox Dark", "catppuccin-mocha": "Catppuccin Mocha",
    "solarized-dark": "Solarized Dark", "gruvbox-light": "Gruvbox Light",
    "catppuccin-latte": "Catppuccin Latte", "solarized-light": "Solarized Light",
}

CREDITS = {
    "nord": "Nord — arcticicestudio/nord, MIT",
    "dracula": "Dracula — dracula/visual-studio-code, MIT",
    "monokai": "Monokai — microsoft/vscode theme-monokai, MIT",
    "gruvbox-dark": "Gruvbox Dark — morhetz/gruvbox, MIT",
    "catppuccin-mocha": "Catppuccin Mocha — catppuccin/palette, MIT",
    "solarized-dark": "Solarized Dark — altercation/solarized, MIT",
    "gruvbox-light": "Gruvbox Light — morhetz/gruvbox, MIT",
    "catppuccin-latte": "Catppuccin Latte — catppuccin/palette, MIT",
    "solarized-light": "Solarized Light — altercation/solarized, MIT",
}

# Everything above this line in themes.css is hand-written — the mapping onto Pico's
# variables, which is a design decision rather than a derived one. Only what follows is
# regenerated, so the two can be reviewed separately.
MARKER = "   Fifteen tokens each. Provenance and licence for every scheme is recorded in NOTICE. */"

STYLESHEET = "src/Boh.Web/wwwroot/css/themes.css"


def validate(name, p):
    """Every pair the mapping actually renders. Returns a list of human-readable failures."""
    problems = []
    for fg, bg, need, what in TEXT_CHECKS + BORDER_CHECKS:
        r = ratio(p[fg], p[bg])
        if r < need - 0.005:
            problems.append(f"{what}: {fg} on {bg} is {r:.2f}, needs {need}")
    for label, fill in INVERSE_PAIRS:
        r = ratio(p[label], p[fill])
        if r < 4.5:
            problems.append(f"{label} on {fill} is {r:.2f}, needs 4.5")

    # Hierarchy invariant. Contrast alone cannot catch this: a muted tone dragged far
    # enough to clear AA against the darkest surface can end up *more* prominent than
    # body text, which reads as backwards even though every ratio passes.
    if ratio(p["fg_muted"], p["bg"]) >= ratio(p["fg"], p["bg"]):
        problems.append("fg_muted is more prominent than fg")
    if ratio(p["fg_strong"], p["bg"]) < ratio(p["fg"], p["bg"]):
        problems.append("fg_strong is less prominent than fg")
    return problems


def main():
    final, blocks, total_moved, failed = {}, [], 0, 0

    for name, raw in PALETTES.items():
        p, moved = adjust(name, raw)
        final[name] = p
        total_moved += len(moved)

        problems = validate(name, p)
        failed += len(problems)
        print(f"{LABELS[name]:<18} {len(moved):>2} adjusted"
              f"{'' if not problems else f', {len(problems)} UNRESOLVED'}")
        for tok, (was, now) in sorted(moved.items()):
            print(f"    {tok:<13} {was} -> {now}")
        for problem in problems:
            print(f"    !! {problem}")

        # :root, not a bare attribute selector. Pico's light-theme variables are declared
        # on `:root:not([data-theme=dark])`, so a plain [data-theme-name] block ties on
        # source order but loses on specificity and every light palette silently no-ops.
        lines = [f"/* {CREDITS[name]} */", f':root[data-theme-name="{name}"] {{']
        lines += [f"    {var}: {p[key]};" for key, var in TOKENS]
        lines.append("}")
        blocks.append("\n".join(lines))

    if failed:
        # Refusing to write is the point of the exercise: a palette that cannot be brought
        # to AA should not reach the stylesheet just because it was added to the list.
        print(f"\n{failed} unresolved check(s); {STYLESHEET} not written")
        return 1

    path = pathlib.Path(__file__).resolve().parent.parent / STYLESHEET
    head = path.read_text().split(MARKER)[0]
    path.write_text(head + MARKER + "\n\n" + "\n\n".join(blocks) + "\n")
    print(f"\n{total_moved} tokens adjusted, all checks pass; wrote {STYLESHEET}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
