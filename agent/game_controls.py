"""Game-agnostic control primitives: screenshot, keyboard+mouse, process mgmt.

One GameControls instance per game, loaded from games/<name>.json.
Used directly by env.py (tight loop) and wrapped by game_mcp.py so any
MCP-capable agent can drive the pointed-at game too.

NOTE: screenshots and input need the interactive desktop. Run this in the
user's session (session 1), not over headless SSH (session 0).
"""
from __future__ import annotations

import base64
import ctypes
import io
import json
import os
import subprocess
import time

import mss
from PIL import Image

import pydirectinput

pydirectinput.PAUSE = 0.02

HERE = os.path.dirname(os.path.abspath(__file__))
GAMES_DIR = os.path.join(HERE, "games")


def load_game(name: str) -> dict:
    path = os.path.join(GAMES_DIR, f"{name}.json")
    with open(path) as f:
        return json.load(f)


class GameControls:
    def __init__(self, game: dict | str):
        self.cfg = load_game(game) if isinstance(game, str) else game
        self.keymap: dict = self.cfg["keys"]

    # ---------- screen ----------

    def screenshot(self, width: int = 800) -> bytes:
        with mss.mss() as sct:
            raw = sct.grab(sct.monitors[1])
            img = Image.frombytes("RGB", raw.size, raw.bgra, "raw", "BGRX")
        w, h = img.size
        img = img.resize((width, int(h * width / w)), Image.BILINEAR)
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        return buf.getvalue()

    def screenshot_data_url(self, width: int = 800) -> str:
        return "data:image/png;base64," + base64.b64encode(
            self.screenshot(width)).decode()

    def screen_fingerprint(self, width: int = 32) -> list:
        with mss.mss() as sct:
            raw = sct.grab(sct.monitors[1])
            img = Image.frombytes("RGB", raw.size, raw.bgra, "raw", "BGRX")
        img = img.resize((width, width), Image.BILINEAR).convert("L")
        return list(img.getdata())

    def screen_size(self) -> tuple:
        with mss.mss() as sct:
            m = sct.monitors[1]
            return (m["width"], m["height"])

    # ---------- input ----------

    def key(self, name: str) -> str:
        return self.keymap.get(name, name)

    def press(self, names, hold: float = 0.15) -> None:
        if isinstance(names, str):
            names = [names]
        keys = [self.key(n) for n in names]
        for k in keys:
            pydirectinput.keyDown(k)
        time.sleep(hold)
        for k in keys:
            pydirectinput.keyUp(k)

    def tap(self, name: str) -> None:
        self.press(name, hold=0.05)

    def move_mouse(self, x: int, y: int) -> None:
        import pyautogui
        pyautogui.moveTo(x, y, duration=0.15)

    def click(self, button: str = "left", x: int | None = None,
              y: int | None = None) -> None:
        import pyautogui
        if x is not None and y is not None:
            pyautogui.click(x=x, y=y, button=button)
        else:
            pyautogui.click(button=button)

    # ---------- window / process ----------

    def _find_window(self):
        hint = self.cfg["window_hint"].lower()
        user32 = ctypes.windll.user32
        found = []

        @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
        def enum_cb(hwnd, _):
            length = user32.GetWindowTextLengthW(hwnd)
            if length:
                buf = ctypes.create_unicode_buffer(length + 1)
                user32.GetWindowTextW(hwnd, buf, length + 1)
                if hint in buf.value.lower():
                    found.append(hwnd)
            return True

        user32.EnumWindows(enum_cb, None)
        return found[0] if found else None

    def focus(self) -> bool:
        hwnd = self._find_window()
        if not hwnd:
            return False
        ctypes.windll.user32.SetForegroundWindow(hwnd)
        time.sleep(0.3)
        return True

    def is_running(self) -> bool:
        proc = self.cfg["game_process"]
        out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {proc}"],
                             capture_output=True, text=True)
        return proc.lower() in out.stdout.lower()

    def launch(self, wait_s: int = 10) -> bool:
        if self.is_running():
            return self.focus()
        exe = self.cfg["game_exe"]
        subprocess.Popen([exe], cwd=os.path.dirname(exe))
        time.sleep(wait_s)
        return self.focus()

    def kill(self) -> None:
        subprocess.run(["taskkill", "/F", "/IM", self.cfg["game_process"]],
                       capture_output=True)
