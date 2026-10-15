"""Builds the MX Creative Keypad profiles (.lp5) that ship with AgentPad.

Profiles are zip files the Logi Options+ "Import profile" button reads. This script writes them from
scratch into profiles/: a General profile (sessions and shortcut board, active in any application)
and one profile per VS Code-based editor that adds editor and code pages.

Run from the repository root:  python tools/make_profiles.py
Requires Pillow for the key previews shown by the import dialog.
"""

import base64
import io
import json
import os
import uuid
import zipfile

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
OUT = os.path.join(ROOT, "profiles")
ICON = os.path.join(ROOT, "src", "package", "metadata", "Icon256x256.png")

PLUGIN = "ClaudeCodeSessions"
ACTIONS = "Loupedeck.ClaudeCodePlugin.Actions."
DEVICE = "Loupedeck70"  # MX Creative Keypad
SEP = "#¤%&+?"  # separator inside Logi action names
LAYOUT_US = 67699721  # keyboard layout id, used only to display shortcuts

SERVICE = "LoupedeckService"
DEVICES = "Loupedeck.Service.Devices.Loupedeck7Devices."


def new_id():
    """Returns a new identifier in the format Logi uses (32 upper-case hex digits)."""
    return uuid.uuid4().hex.upper()


# ---------------------------------------------------------------- Keys

def slot(n):
    """Session tile n (a dynamic folder of the plugin)."""
    name = f"SessionSlot{n:02d}DynamicFolder"
    return ("action", f"${PLUGIN}___#DynamicFolder___DynamicFolder#{ACTIONS}{name}")


def board(n):
    """Key n of the shortcut board (1 is the session count)."""
    return ("action", f"${PLUGIN}___{ACTIONS}ShortcutBoardCommand___{n}")


def keys(code, display):
    """A keyboard shortcut, e.g. keys("ControlOrCommand+KeyP", "Ctrl+P")."""
    return ("keys", (code, display))


# AgentPad adds these Ctrl+Alt+Shift+F13..F19 bindings to the editors: function keys, unlike
# punctuation keys, are the same on every keyboard layout.
CLAUDE_FOCUS = "ControlOrCommand+Shift+AltOrOption+F13"
COMMENT = "ControlOrCommand+Shift+AltOrOption+F17"
TERMINAL = "ControlOrCommand+Shift+AltOrOption+F18"
QUICK_FIX = "ControlOrCommand+Shift+AltOrOption+F19"

CLAUDE_PAGES = [
    ("Claude", [(f"Session {n}", *slot(n)) for n in range(1, 10)]),
    ("Board", [("Sessions", *board(1))] +
     [(f"Shortcut {n - 1}", *board(n)) for n in range(2, 10)]),
]

EDITOR_PAGES = [
    ("Editor", [
        ("Go to file", *keys("ControlOrCommand+KeyP", "Ctrl+P")),
        ("Commands", *keys("ControlOrCommand+Shift+KeyP", "Ctrl+Shift+P")),
        ("Search", *keys("ControlOrCommand+Shift+KeyF", "Ctrl+Shift+F")),
        ("Replace", *keys("ControlOrCommand+KeyH", "Ctrl+H")),
        ("Close tab", *keys("ControlOrCommand+KeyW", "Ctrl+W")),
        ("Comment", *keys(COMMENT, "Comment")),
        ("Terminal", *keys(TERMINAL, "Terminal")),
        ("Git", *keys("ControlOrCommand+Shift+KeyG", "Ctrl+Shift+G")),
        ("Claude input", *keys(CLAUDE_FOCUS, "Claude")),
    ]),
    ("Code", [
        ("Save", *keys("ControlOrCommand+KeyS", "Ctrl+S")),
        ("Format", *keys("Shift+AltOrOption+KeyF", "Shift+Alt+F")),
        ("Comment", *keys(COMMENT, "Comment")),
        ("Definition", *keys("F12", "F12")),
        ("Rename", *keys("F2", "F2")),
        ("References", *keys("Shift+F12", "Shift+F12")),
        ("Quick fix", *keys(QUICK_FIX, "Quick fix")),
        ("Sidebar", *keys("ControlOrCommand+KeyB", "Ctrl+B")),
        ("Run / Debug", *keys("F5", "F5")),
    ]),
]

# (file, profile name, application) - application None is the General profile
PROFILES = [
    ("AgentPad - General.lp5", "AgentPad", None),
    ("AgentPad - Antigravity IDE.lp5", "AgentPad",
     {"name": "antigravity ide", "displayName": "Antigravity IDE"}),
    ("AgentPad - VS Code.lp5", "AgentPad", {"name": "code", "displayName": "Visual Studio Code"}),
    ("AgentPad - Cursor.lp5", "AgentPad", {"name": "cursor", "displayName": "Cursor"}),
    ("AgentPad - Windsurf.lp5", "AgentPad", {"name": "windsurf", "displayName": "Windsurf"}),
]


# ---------------------------------------------------------------- Profile documents

def keyboard_action(label, code, display):
    """A profile action that presses a keyboard shortcut."""
    return {
        "$type": f"Loupedeck.Service.ApplicationProfileCommand, {SERVICE}",
        "isCommand": True,
        "name": f"$@Generic___@ProfileAction___{new_id()}",
        "templateActionName": "$@Generic___@KeyboardKey",
        "actionParameters": {
            "$type": "Loupedeck.ActionEditorActionParameters, PluginApi",
            "parameters": {
                "$type": "Loupedeck.StringDictionaryNoCase, PluginApi",
                "keyboardKey": f"{code}___{LAYOUT_US}___{display}___",
            },
            "count": 1,
        },
        "displayName": label,
        "description": "",
        "groupName": "",
        "superGroupName": "@macro",
        "isProfileAction": True,
        "isMultiState": False,
        "isResetCommand": False,
        "adjustmentName": None,
        "states": None,
    }


def profile_info(name, app, pages, profile_id):
    """The profile document: pages of 9 keys and the shortcuts they press."""
    actions = []
    press_pages = []
    for page_name, page_keys in pages:
        controls = []
        for control_id, (label, kind, value) in enumerate(page_keys):
            if kind == "keys":
                action = keyboard_action(label, *value)
                actions.append(action)
                press = action["name"]
            else:
                press = value
            controls.append({
                "$type": f"{DEVICES}ProfileLayoutControl7, {SERVICE}",
                "controlId": control_id,
                "pressAction": press,
                "rotateAction": None,
            })
        press_pages.append({
            "$type": f"{DEVICES}ProfileLayoutPage7, {SERVICE}",
            "name": new_id(),
            "displayName": page_name,
            "description": None,
            "controls": controls,
        })

    workspace = new_id()
    return {
        "$type": f"Loupedeck.Service.ApplicationProfile, {SERVICE}",
        "name": profile_id,
        "profileFlags": "None",
        "displayName": name,
        "description": "Claude Code sessions on the MX Keypad (AgentPad for IDEs)",
        "deviceType": DEVICE,
        "applicationName": app["name"] if app else "@_defaultwin",
        "nativePluginName": None if app else "DefaultWin",
        "hasNativePlugin": app is None,
        "additionalNativePluginNames": [PLUGIN],
        "lastModifiedTimeUtc": "2026-10-15T00:00:00Z",
        "profileSettings": {
            "$type": "Loupedeck.DictionaryNoCase`1[[System.String, "
                     "System.Private.CoreLib]], PluginApi",
        },
        "actionImages90": None,
        "actionImages60": None,
        "wheelImages": None,
        "actionColors": None,
        "layout": {
            "$type": f"{DEVICES}ProfileLayout7, {SERVICE}",
            "deviceType": DEVICE,
            "profileFlags": "None",
            "layoutModes": [{
                "$type": f"{DEVICES}ProfileLayoutMode7, {SERVICE}",
                "deviceType": DEVICE,
                # The General profile runs in the system plugin's mode, applications in "main"
                "modeName": "main" if app else "System",
                "parentModeName": None,
                "actions": None,
                "dynamicButtonPages": None,
                "dynamicEncoderPages": None,
                "workspaces": [{
                    "$type": f"{DEVICES}ProfileLayoutWorkspace7, {SERVICE}",
                    "name": workspace,
                    "displayName": "Workspace 1",
                    "description": None,
                    "pressPages": press_pages,
                    "rotatePages": [],
                }],
                "homeWorkspaceName": workspace,
            }],
        },
        "macroCommands": [],
        "macroAdjustments": [],
        "profileCommands": [],
        "profileAdjustments": [],
        "conversionHistory": "",
        "packageName": new_id(),
        "packageVersion": "1.0.0.0",
        "profileActions": actions,
    }


def application_info(app, profile_id):
    """The application the profile belongs to (the system plugin for the General profile)."""
    if app is None:
        return {
            "$type": f"Loupedeck.Service.SupportedApplicationInfo, {SERVICE}",
            "name": "@_defaultwin",
            "displayName": "System plugin",
            "description": None,
            "deviceType": DEVICE,
            "nativePluginName": "DefaultWin",
            "hasNativePlugin": True,
            "processOrBundleName": None,
            "modes": [{
                "$type": f"Loupedeck.Service.ApplicationMode, {SERVICE}",
                "name": "System", "parentModeName": None, "displayName": "System",
            }],
            "defaultProfileName": profile_id,
            "isEnabled": True,
        }
    return {
        "$type": f"Loupedeck.Service.SupportedApplicationInfo, {SERVICE}",
        "name": app["name"],
        "displayName": app["displayName"],
        "description": None,
        "deviceType": DEVICE,
        "nativePluginName": None,
        "hasNativePlugin": False,
        "processOrBundleName": app["name"],
        "modes": [{
            "$type": f"Loupedeck.Service.ApplicationMode, {SERVICE}",
            "name": "main", "parentModeName": None, "displayName": "Main",
        }],
        "defaultProfileName": profile_id,
        "isEnabled": True,
    }


def preview(pages):
    """Key images of the first page for the import dialog."""
    try:
        font = ImageFont.truetype("segoeui.ttf", 18)
    except OSError:
        font = ImageFont.load_default()
    images = []
    for control_id, (label, kind, value) in enumerate(pages[0][1]):
        img = Image.new("RGB", (116, 116), (38, 38, 36))
        draw = ImageDraw.Draw(img)
        width = draw.textlength(label, font=font)
        draw.text(((116 - width) / 2, 46), label, fill="white", font=font)
        buffer = io.BytesIO()
        img.save(buffer, "PNG")
        images.append({
            "controlId": str(control_id),
            "actionName": value if kind == "action" else "",
            "displayName": label,
            "description": "",
            "image": base64.b64encode(buffer.getvalue()).decode(),
        })
    return {"buttonPages": images, "encoderPages": []}


def app_icon():
    """The AgentPad icon at 64 px, shown for the profile's application."""
    buffer = io.BytesIO()
    Image.open(ICON).resize((64, 64)).save(buffer, "PNG")
    return buffer.getvalue()


def build(file_name, name, app):
    """Writes one profile file into profiles/."""
    pages = CLAUDE_PAGES + ([] if app is None else EDITOR_PAGES)
    profile_id = new_id()
    files = {
        "ProfileInfo.json": profile_info(name, app, pages, profile_id),
        "ApplicationInfo.json": application_info(app, profile_id),
        "metadata/AdvancedInfo.json": {"additionalPluginNames": [PLUGIN]},
        "metadata/ProfilePreview.json": preview(pages),
    }
    path = os.path.join(OUT, file_name)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for entry, doc in files.items():
            z.writestr(entry, json.dumps(doc, indent=2, ensure_ascii=False))
        z.writestr("metadata/LoupedeckPackage.yaml",
                   f"type: Profile5\nname: {profile_id}\ndisplayName: {name}\n"
                   "version: 1.0.0.0\n")
        z.writestr("ApplicationIcon.png", app_icon())
    return path


def main():
    os.makedirs(OUT, exist_ok=True)
    for file_name, name, app in PROFILES:
        print("written", build(file_name, name, app))


if __name__ == "__main__":
    main()
