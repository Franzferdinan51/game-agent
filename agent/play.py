"""Play any configured game via SystemOne's DecisionLoop (See > Decide > Act).

    python play.py --game sonic [--ticks 60] [--model qwen2.5-vl-3b-instruct]

Judge = D1 (or any LM Studio vision model) via chat completions. Returns
SystemOne-shaped answers so DecisionLoop's gates, StallGuard and escalation
all still apply. Swap models via the game config's "model" field or --model.
(The shim's local engine is text-only and drops images, so vision goes
straight to LM Studio; the loop itself is pure SystemOne.)

Hand-in-hand hooks (so Juno can collaborate mid-run):
- runs/<game>_<ts>.jsonl  : every tick logged (action, confidence, progress)
- runs/<game>_<ts>/tick_N.png : screenshot the judge saw each tick
- runs/<game>_<ts>/status.json : live status (tick, last action, outcome)
- coach_<game>.md is RE-READ every 5 ticks: edit it mid-run to steer.
- drop a file named PAUSE in the run dir -> loop waits until you delete it.
  (grab the wheel yourself via the MCP tools while paused.)
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r"C:\Games\SystemOne")

from game_controls import GameControls, load_game
from env import GameEnv
from systemone.loop import DecisionLoop

HERE = os.path.dirname(os.path.abspath(__file__))
COACH_RELOAD_EVERY = 5
LMSTUDIO_URL = "http://127.0.0.1:1234"


def post_json(url: str, body: dict, timeout: int = 180) -> dict:
    req = urllib.request.Request(
        url, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"}, method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read())


def read_coach(game: str, extra: str | None) -> str:
    text = ""
    paths = ([extra] if extra else []
             + [os.path.join(HERE, f"coach_{game}.md")])
    for p in paths:
        if p and os.path.isfile(p):
            with open(p) as f:
                text += f.read().strip() + "\n\n"
    return text


def make_judge(lmstudio_url: str, cfg: dict, model: str, coach_state: dict):
    """Vision judge via LM Studio. D1 is the decision model; swap via config.
    Returns SystemOne-shaped answers."""
    actions_desc = "\n".join(
        f'- "{n}": {s.get("desc", n)}' for n, s in cfg["actions"].items())
    valid = set(cfg["actions"])

    def judge(state_text, questions, images, videos):
        coach = coach_state.get("text", "")
        system = (
            (f"COACH INSTRUCTIONS (override defaults):\n{coach}\n" if coach else "")
            + f"GOAL: {cfg['goal']}\nRULES:\n"
            + "\n".join(f"- {r}" for r in cfg["rules"])
            + f"\n\nACTIONS:\n{actions_desc}\n\n"
            "Pick exactly one action. Reply with ONLY this JSON, nothing else:\n"
            '{"choice": "<action_name>", "confidence": 0.0-1.0, '
            '"reason": "<10 words max>"}'
        )
        content = [{"type": "text", "text": state_text}]
        for img_url in images or []:
            content.append({"type": "image_url",
                            "image_url": {"url": img_url}})
        body = {
            "model": model,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": content},
            ],
            "max_tokens": 120,
            "temperature": 0.2,
        }
        t0 = time.time()
        try:
            payload = post_json(f"{lmstudio_url}/v1/chat/completions", body)
            raw = payload["choices"][0]["message"]["content"].strip()
            if raw.startswith("```"):
                raw = raw.strip("`").split("\n", 1)[-1].rsplit("```", 1)[0]
            ans = json.loads(raw)
            choice = str(ans.get("choice", "wait"))
            conf = float(ans.get("confidence", 0.0) or 0.0)
        except Exception:
            choice, conf = "wait", 0.0
        if choice not in valid:
            choice, conf = "wait", 0.0
        return {
            "action": {
                "type": "choice",
                "choice": choice,
                "probabilities": {},
                "confidence": max(0.0, min(1.0, conf)),
                "label_mass": None,
            },
            "_meta": {"backend": f"lmstudio:{model}",
                      "latency_ms": (time.time() - t0) * 1000},
        }

    return judge


class CollabEnv(GameEnv):
    """GameEnv + run logging, screenshots, pause flag, coach reload."""

    def __init__(self, game: str, run_dir: str, coach_state: dict,
                 shot_width: int = 800):
        super().__init__(game, shot_width)
        self.run_dir = run_dir
        self.coach_state = coach_state
        self.game_name = game
        self.log_path = os.path.join(run_dir, "run.jsonl")
        self.status_path = os.path.join(run_dir, "status.json")
        self.pause_path = os.path.join(run_dir, "PAUSE")
        self._write_status("running", None)

    def _write_status(self, state: str, last: dict | None):
        with open(self.status_path, "w") as f:
            json.dump({"state": state, "tick": self.tick,
                       "last": last, "game": self.game_name}, f)

    def _wait_if_paused(self):
        while os.path.exists(self.pause_path):
            self._write_status("paused", None)
            time.sleep(2)

    def observe(self):
        self._wait_if_paused()
        if self.tick % COACH_RELOAD_EVERY == 0:
            self.coach_state["text"] = read_coach(
                self.game_name, self.coach_state.get("extra"))
        obs = super().observe()
        shot = self.gc.screenshot(self.shot_width)
        with open(os.path.join(self.run_dir, f"tick_{self.tick}.png"),
                  "wb") as f:
            f.write(shot)
        return obs

    def act(self, action: str):
        result = super().act(action)
        entry = {"tick": self.tick, "action": action,
                 "progressed": result.progressed, "info": result.info}
        with open(self.log_path, "a") as f:
            f.write(json.dumps(entry) + "\n")
        self._write_status("running", entry)
        return result


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default="sonic")
    ap.add_argument("--ticks", type=int, default=60)
    ap.add_argument("--lmstudio", default=LMSTUDIO_URL)
    ap.add_argument("--model", default=None,
                    help="LM Studio model id (overrides game config)")
    ap.add_argument("--no-launch", action="store_true")
    ap.add_argument("--coach", default=None)
    args = ap.parse_args()

    cfg = load_game(args.game)
    model = args.model or cfg.get("model", "d1-3b")
    gc = GameControls(cfg)

    if not args.no_launch:
        print(f"launching {cfg['name']}...", flush=True)
        if not gc.launch():
            print("WARNING: game window not found after launch.")
    else:
        gc.focus()

    ts = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    run_dir = os.path.join(HERE, "runs", f"{args.game}_{ts}")
    os.makedirs(run_dir, exist_ok=True)

    coach_state = {"text": read_coach(args.game, args.coach),
                   "extra": args.coach}
    env = CollabEnv(args.game, run_dir, coach_state)
    loop = DecisionLoop(
        make_judge(args.lmstudio, cfg, model, coach_state),
        action_name="action",
        uncertainty_action="wait",
        confidence_floor=0.35,
        budget=args.ticks,
        system_prompt=cfg["goal"],
    )
    print(f"model={model} run_dir={run_dir}", flush=True)
    print("coach live-reload every 5 ticks; drop PAUSE in run dir to hold.",
          flush=True)
    result = loop.run(env, [{"name": "action", "type": "choice"}])
    env._write_status(result.outcome, None)
    print(f"\noutcome: {result.outcome}  ticks: {result.n_ticks}")
    for s in result.steps:
        flag = "GATED" if s.gated else ""
        print(f"  tick {s.tick:>3}: {s.action:<14} conf={s.confidence:.2f} "
              f"{'PROG' if s.progressed else 'stall'} {flag}")


if __name__ == "__main__":
    main()
