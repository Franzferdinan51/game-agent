"""Game-agnostic SystemOne Env: SEE screenshots, ACT named actions.

Implements systemone.loop.Env so DecisionLoop runs See > Decide > Act
against whatever game the config points at. Progress = screen changed
between ticks (stall proxy).
"""
from __future__ import annotations

import base64
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r"C:\Games\SystemOne")

from game_controls import GameControls
from systemone.loop import ActResult, Observation

STALL_THRESHOLD = 6.0  # mean abs pixel diff below this = stalled


class GameEnv:
    def __init__(self, game: str, shot_width: int = 800):
        self.gc = GameControls(game)
        self.cfg = self.gc.cfg
        self.shot_width = shot_width
        self.tick = 0
        self.prev_fp = None

    def _screen_changed(self) -> bool:
        fp = self.gc.screen_fingerprint()
        if self.prev_fp is None:
            self.prev_fp = fp
            return True
        diff = sum(abs(a - b) for a, b in zip(fp, self.prev_fp)) / len(fp)
        self.prev_fp = fp
        return diff > STALL_THRESHOLD

    def observe(self) -> Observation:
        shot = self.gc.screenshot(self.shot_width)
        url = "data:image/png;base64," + base64.b64encode(shot).decode()
        self.tick += 1
        text = (
            f"{self.cfg['name']} gameplay screenshot (tick {self.tick}). "
            "You are playing. Read the screen carefully before choosing."
        )
        return Observation(text=text, images=[url])

    def act(self, action: str) -> ActResult:
        spec = self.cfg["actions"].get(action)
        if spec is None:
            return ActResult(progressed=False,
                             info={"error": f"unknown action {action}"})
        keys, hold = spec["keys"], spec.get("hold", 0.3)
        if keys:
            self.gc.press(keys, hold=hold)
        else:
            time.sleep(hold)
        time.sleep(0.25)  # let the game react before next screenshot
        progressed = self._screen_changed()
        return ActResult(progressed=progressed, info={"action": action})
