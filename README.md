# game-agent — point at any game, play it with SystemOne

All on Batman-3. No cloud, no Mac needed.

## pieces

| file | what |
|---|---|
| `games/sonic.json` | Sonic Unleashed: exe, keys, actions, goal, rules, model |
| `games/stardew.json` | Stardew Valley: same |
| `game_controls.py` | screenshot, keyboard, mouse, launch/kill/focus |
| `game_mcp.py` | MCP server wrapping the controls (stdio / sse / streamable-http) |
| `env.py` | SystemOne `Env`: SEE screenshots, ACT named actions |
| `play.py` | SystemOne `DecisionLoop` (See > Decide > Act) via the shim |
| `coach_<game>.md` | coach notes, live-reloaded every 5 ticks |
| `vision_check.py` | quick "can this model see?" probe via LM Studio |
| `start_shim.bat` | launches the SystemOne shim on :8765 |

## the loop

```
SEE  screenshot -> SystemOne Observation (text + image)
DECIDE  POST shim /v1/systemone {model, choice over actions, images:[data-url]}
ACT  press keys -> screen-change check (stall proxy)
```

Uncertainty gate: confidence < 0.35 -> `wait`. StallGuard stops after 3 stalls.

## running it

**1. vision model (one-time GUI step):** LM Studio on Batman-3 ->
load `d1-3B` (already in `.lmstudio/models`, mmproj included — it's the
SystemOne decision model and it sees), then start LM Studio's local server
(localhost:1234). d1-omni-600M is the fallback. (Qwen2.5-VL-3B is also
downloaded as a spare.)

**2. shim** (interactive session — screenshots need the real desktop):
```
schtasks /create /tn "GameAgent-shim" /tr "C:\Games\sonic-agent\start_shim.bat" /sc onlogon /it /f
schtasks /run /tn "GameAgent-shim"
```

**3. mcp** (registered, auto-starts at logon):
```
GameAgent-MCP-sonic    ->  http://127.0.0.1:8931/mcp  (streamable-http)
GameAgent-MCP-stardew  ->  http://127.0.0.1:8932/mcp  (streamable-http)
```
Juno drives these over HTTP from SSH. Any MCP client can connect.

**4. play:**
```
cd C:\Games\sonic-agent
python play.py --game sonic --ticks 60
python play.py --game stardew --ticks 120 --model qwen2.5-vl-3b-instruct
```
(must run in the interactive session — use another scheduled task or run it
from the desktop while logged in.)

## swap the brain

the judge model is one line: `"model": "d1-3b"` in `games/<name>.json`,
or `--model <id>` on the command line. any vision model in LM Studio works —
d1-3b, qwen2.5-vl-3b-instruct, whatever you load. no code changes.

## hand in hand

the loop isn't on its own — juno works with it:
- `coach_sonic.md` / `coach_stardew.md` are read at start AND re-read every
  5 ticks, prepended as overriding instructions. edit them mid-run to steer.
  `--coach path/to/file.md` adds another.
- juno watches live through the MCP `screenshot` tool and grabs the wheel
  with `do_action` / `hold_keys` / `click_at` any time — coach + player.
- every run drops into `runs/<game>_<timestamp>/`: `run.jsonl` (every tick),
  `tick_N.png` (exactly what the judge saw), `status.json` (live state).
  juno reads these to review and coach the next run.
- drop a file named `PAUSE` in the run dir and the loop holds until you
  delete it — take over manually via MCP, then resume.
- between runs: read the tick log, update coach notes, run again. compounds.

## notes

- keymap is logical->physical; match it to the game's real bindings.
- stardew needs mouse for tools: use the MCP `click_at` tool for that.
- sonic is fast — the loop runs a few seconds per tick. expect wall-eating
  until tick speed and coaching tighten up. stardew's pace suits it better.
