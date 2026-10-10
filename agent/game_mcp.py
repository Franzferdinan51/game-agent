"""MCP server: point it at any game, drive it from any MCP-capable agent.

    python game_mcp.py --game sonic                       # stdio (default)
    python game_mcp.py --game sonic --transport sse --port 8931
    python game_mcp.py --game stardew --transport streamable-http --port 8932

Tools: screenshot, tap_key, hold_keys, move_mouse, click_at, click,
       launch_game, kill_game, focus_game, game_running, get_keymap,
       get_actions, do_action
"""
from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mcp.server.fastmcp import FastMCP
from mcp.types import ImageContent

from game_controls import GameControls

ap = argparse.ArgumentParser()
ap.add_argument("--game", default="sonic")
ap.add_argument("--transport", default="stdio",
                choices=["stdio", "sse", "streamable-http"])
ap.add_argument("--port", type=int, default=8931)
args = ap.parse_args()

gc = GameControls(args.game)
mcp = FastMCP(f"game-control-{args.game}", port=args.port)


@mcp.tool()
def screenshot() -> ImageContent:
    """Capture the current screen as a PNG image."""
    import base64
    return ImageContent(
        type="image",
        data=base64.b64encode(gc.screenshot()).decode(),
        mimeType="image/png",
    )


@mcp.tool()
def tap_key(key: str) -> str:
    """Tap a single key (logical name from keymap, e.g. 'jump', 'up')."""
    gc.tap(key)
    return f"tapped {key} -> {gc.key(key)}"


@mcp.tool()
def hold_keys(keys: list[str], hold_seconds: float = 0.4) -> str:
    """Hold one or more keys simultaneously, then release."""
    gc.press(keys, hold=hold_seconds)
    resolved = [gc.key(k) for k in keys]
    return f"held {resolved} for {hold_seconds}s"


@mcp.tool()
def move_mouse(x: int, y: int) -> str:
    """Move the mouse cursor to screen coordinates x, y."""
    gc.move_mouse(x, y)
    return f"mouse -> ({x}, {y})"


@mcp.tool()
def click_at(x: int, y: int, button: str = "left") -> str:
    """Click at screen coordinates x, y."""
    gc.click(button=button, x=x, y=y)
    return f"clicked ({x}, {y}) [{button}]"


@mcp.tool()
def click(button: str = "left") -> str:
    """Click wherever the cursor is."""
    gc.click(button=button)
    return f"clicked [{button}]"


@mcp.tool()
def launch_game() -> str:
    """Launch the game if not running, and focus its window."""
    ok = gc.launch()
    return "game launched and focused" if ok else "launched but window not found"


@mcp.tool()
def kill_game() -> str:
    """Force-close the game."""
    gc.kill()
    return "game killed"


@mcp.tool()
def focus_game() -> str:
    """Bring the game window to the front."""
    return "focused" if gc.focus() else "window not found"


@mcp.tool()
def game_running() -> str:
    """Check whether the game process is running."""
    return "running" if gc.is_running() else "not running"


@mcp.tool()
def get_keymap() -> dict:
    """Return the logical-name -> physical-key mapping."""
    return gc.keymap


@mcp.tool()
def get_actions() -> dict:
    """Return the named actions available for this game."""
    return {n: s.get("desc", "") for n, s in gc.cfg["actions"].items()}


@mcp.tool()
def do_action(action: str) -> str:
    """Perform a named game action (see get_actions)."""
    spec = gc.cfg["actions"].get(action)
    if not spec:
        return f"unknown action: {action}"
    keys, hold = spec["keys"], spec.get("hold", 0.3)
    if keys:
        gc.press(keys, hold=hold)
    else:
        import time
        time.sleep(hold)
    return f"did {action}"


if __name__ == "__main__":
    if args.transport == "stdio":
        mcp.run()
    else:
        mcp.run(transport=args.transport)
