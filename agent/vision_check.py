"""Vision check: ask d1-3b (via LM Studio on Batman-3) what it sees."""
import json, base64, urllib.request, sys

img_path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Windows\Temp\vision_test.png"
model = sys.argv[2] if len(sys.argv) > 2 else "d1-3b"
question = sys.argv[3] if len(sys.argv) > 3 else "What do you see? Reply in one short sentence."

img = base64.b64encode(open(img_path, "rb").read()).decode()
body = {
    "model": model,
    "messages": [{"role": "user", "content": [
        {"type": "text", "text": question},
        {"type": "image_url", "image_url": {"url": "data:image/png;base64," + img}},
    ]}],
    "max_tokens": 100,
}
req = urllib.request.Request(
    "http://127.0.0.1:1234/v1/chat/completions",
    data=json.dumps(body).encode(),
    headers={"Content-Type": "application/json"},
)
with urllib.request.urlopen(req, timeout=180) as r:
    out = json.loads(r.read())
print(out["choices"][0]["message"]["content"])
