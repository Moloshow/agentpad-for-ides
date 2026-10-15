"""Writes the action icons of the plugin package (32x32 white line icons, SVG).

Also writes the key template the Logi Plugin Service draws every key through.

Run from the repository root:  python tools/make_icons.py
"""
import json
import os

ROOT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "src", "package")
NS = "Loupedeck.ClaudeCodePlugin.Actions."

STROKE = ('stroke="white" stroke-width="2.4" stroke-linecap="round" '
          'stroke-linejoin="round" fill="none"')


def svg(body):
    """Wraps SVG elements in a 32x32 SVG document."""
    return ('<svg width="32" height="32" viewBox="0 0 32 32" '
            f'xmlns="http://www.w3.org/2000/svg">{body}</svg>')


def tile():
    """A keypad tile with a dot: a session."""
    return svg(f'<rect x="5" y="5" width="22" height="22" rx="5" {STROKE}/>'
               '<circle cx="16" cy="16" r="3.5" fill="white"/>')


def ring(label):
    """A gauge with a short label in the middle."""
    return svg(f'<circle cx="16" cy="16" r="11" {STROKE}/>'
               '<path d="M16 5 A11 11 0 0 1 26.5 19" stroke="white" '
               'stroke-width="4" fill="none"/>'
               '<text x="16" y="19.5" font-size="8.5" '
               'font-family="Segoe UI, Arial" font-weight="700" fill="white" '
               f'text-anchor="middle">{label}</text>')


def clock():
    """A clock face: a reset time."""
    return svg(f'<circle cx="16" cy="16" r="11" {STROKE}/>'
               f'<path d="M16 9 V16 L21 19" {STROKE}/>')


def check(double=False):
    """A check mark, or two overlapping ones when double is true."""
    if double:
        return svg(f'<path d="M3 17 L8 22 L16 11" {STROKE}/>'
                   f'<path d="M14 20 L16 22 L28 9" {STROKE}/>')
    return svg(f'<path d="M7 16.5 L13 22.5 L25 9.5" {STROKE}/>')


def cross():
    """A diagonal cross: deny."""
    return svg(f'<path d="M9 9 L23 23 M23 9 L9 23" {STROKE}/>')


def bell():
    """A bell: the next session waiting for input."""
    return svg(f'<path d="M10 22 V15 a6 6 0 0 1 12 0 V22 Z" {STROKE}/>'
               f'<path d="M8 22 H24 M14 25.5 H18" {STROKE}/>')


def plus():
    """A window with a plus sign: a new conversation."""
    return svg(f'<rect x="5" y="7" width="22" height="18" rx="4" {STROKE}/>'
               f'<path d="M16 11 V21 M11 16 H21" {STROKE}/>')


def window():
    """A window with a title bar: focus the editor."""
    return svg(f'<rect x="5" y="7" width="22" height="18" rx="3" {STROKE}/>'
               f'<path d="M5 12 H27" {STROKE}/>')


def refresh():
    """A circular arrow: refresh."""
    return svg(f'<path d="M24 12 A9 9 0 1 0 25 18" {STROKE}/>'
               f'<path d="M25 6 V12 H19" {STROKE}/>')


def tokens():
    """A stack of coins: token usage."""
    return svg(f'<ellipse cx="16" cy="10" rx="9" ry="3.5" {STROKE}/>'
               '<path d="M7 10 V22 c0 2 4 3.5 9 3.5 s9-1.5 9-3.5 V10 '
               f'M7 16 c0 2 4 3.5 9 3.5 s9-1.5 9-3.5" {STROKE}/>')


def board():
    """A 3x3 grid of filled squares: the shortcut board."""
    cells = "".join(
        f'<rect x="{5 + 8 * (i % 3)}" y="{5 + 8 * (i // 3)}" width="6" '
        'height="6" rx="1.5" fill="white"/>'
        for i in range(9))
    return svg(cells)


ICONS = {
    "ContextWindowCommand": ring("ctx"),
    "TokenUsageCommand": tokens(),
    "NextWaitingCommand": bell(),
    "RefreshSessionsCommand": refresh(),
    "AllowOnceCommand": check(),
    "AllowConversationCommand": check(double=True),
    "DenyCommand": cross(),
    "NewConversationCommand": plus(),
    "FocusEditorCommand": window(),
    "FiveHourLimitCommand": ring("5h"),
    "WeeklyLimitCommand": ring("7d"),
    "FiveHourResetTimeCommand": clock(),
    "WeeklyResetTimeCommand": clock(),
    "ShortcutBoardCommand": board(),
}


def main():
    """Rewrites the icon folders and the default key template."""
    for folder in ("actionicons", "actionsymbols"):
        path = os.path.join(ROOT, folder)
        os.makedirs(path, exist_ok=True)
        for name in os.listdir(path):
            os.remove(os.path.join(path, name))
        for cls, image in ICONS.items():
            with open(os.path.join(path, NS + cls + ".svg"), "w",
                      encoding="utf-8") as f:
                f.write(image)
        for n in range(1, 17):
            folder_name = f"SessionSlot{n:02d}DynamicFolder"
            for name in (NS + folder_name,
                         f"#DynamicFolder___DynamicFolder#{NS}{folder_name}"):
                with open(os.path.join(path, name + ".svg"), "w",
                          encoding="utf-8") as f:
                    f.write(tile())

    # Key template of the service: the action's image over the whole key, on
    # warm charcoal. The service draws every key of the plugin through it, so
    # it must hold an image item.
    template = {
        "backgroundColor": 4280690212,
        "items": [{
            "image": "", "imageFileName": None, "imageColor": 4294967295,
            "imageRotation": "None", "isVisible": True, "itemType": "Image",
            "area": {"x": 0, "y": 0, "width": 100, "height": 100},
        }],
    }
    with open(os.path.join(ROOT, "metadata", "DefaultIconTemplate.ict"), "w",
              encoding="utf-8") as f:
        json.dump(template, f, indent=2)
    print("icons written")


if __name__ == "__main__":
    main()
