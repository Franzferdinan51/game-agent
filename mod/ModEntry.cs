using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;
using xTile.Dimensions;

namespace GameAgentMod
{
    /// <summary>GameAgent v2: HTTP control + console UI + LM Studio brain.
    /// The bot actually plays: uses tools, tills, plants, waters, harvests.
    /// User controls via SMAPI console: agent_chat, agent_goal, agent_goto, agent_tool, agent_stop.
    /// LM Studio defaults to 127.0.0.1:1234 (local only, never tailnet).</summary>
    public class ModEntry : Mod
    {
        private HttpListener listener;
        private string moveDir;
        private int moveTilesLeft;
        private readonly object moveLock = new object();
        private string currentGoal = "";
        private string lmstudioUrl = "http://127.0.0.1:1234";
        private string lmstudioModel = "d1-3b";

        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            // console commands = backup UI (SMAPI terminal window, not in-game)
            helper.ConsoleCommands.Add("agent_chat", "Talk to the bot: agent_chat <message>", OnChatCommand);
            helper.ConsoleCommands.Add("agent_goal", "Set a goal: agent_goal <goal text>", OnGoalCommand);
            helper.ConsoleCommands.Add("agent_goto", "Walk to tile: agent_goto <x> <y>", OnGotoCommand);
            helper.ConsoleCommands.Add("agent_tool", "Use hotbar tool: agent_tool <slot 1-12>", OnToolCommand);
            helper.ConsoleCommands.Add("agent_stop", "Stop the bot", OnStopCommand);
            helper.ConsoleCommands.Add("agent_lmstudio", "Set LM Studio: agent_lmstudio <url> <model>", OnLmstudioCommand);
            StartHttp();
            Monitor.Log("GameAgent v2 loaded. Press T in-game for DuckBot chat. Console: agent_chat, agent_goal, etc.", LogLevel.Info);
        }

        /// <summary>Press T to open the DuckBot chat UI.</summary>
        private void OnButtonPressed(object sender, StardewModdingAPI.Events.ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            if (e.Button == SButton.T && Game1.activeClickableMenu == null)
            {
                Game1.activeClickableMenu = new ChatMenu(this);
                Game1.playSound("coin");
            }
        }

        /// <summary>Handle a chat message from the UI. Replies via callback on game thread.</summary>
        public void HandleChat(string message, Action<string> onReply)
        {
            Monitor.Log($"[You -> Bot]: {message}", LogLevel.Info);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string reply = AskLmstudio(
                        $"You are DuckBot, a Stardew Valley helper. Player says: \"{message}\". " +
                        $"Game state: {GetStateJson()}. Current goal: {currentGoal}. " +
                        "Reply with ONLY JSON: {\"action\":\"move|tool|interact|none\", " +
                        "\"direction\":\"up|down|left|right\", \"tiles\":1-3, \"slot\":1-12, \"say\":\"<your reply>\"}. " +
                        "Keep say brief and in character.");
                    // parse say + execute
                    string say = reply;
                    try
                    {
                        string clean = reply.StartsWith("```")
                            ? reply.Trim('`').Split('\n', 2)[1].Split("```")[0] : reply;
                        var doc = JsonDocument.Parse(clean);
                        say = doc.RootElement.TryGetProperty("say", out var se) ? se.GetString() : reply;
                        ExecutePlan(clean);
                    }
                    catch { /* use raw reply */ }
                    Monitor.Log($"[Bot]: {say}", LogLevel.Info);
                    // marshal back to game thread for UI update
                    string sayCopy = say;
                    Helper.Events.GameLoop.UpdateTicked += OnReplyTick;
                    void OnReplyTick(object s, StardewModdingAPI.Events.UpdateTickedEventArgs ev)
                    {
                        Helper.Events.GameLoop.UpdateTicked -= OnReplyTick;
                        onReply(sayCopy);
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Chat failed: {ex.Message}", LogLevel.Error);
                    onReply("Sorry, my brain (LM Studio) isn't responding. Is it running?");
                }
            });
        }

        public void SetGoal(string goal)
        {
            currentGoal = goal;
            Monitor.Log($"[Goal set]: {goal}", LogLevel.Info);
        }

        public string GetGoal() => currentGoal;

        #region Console UI

        private void OnChatCommand(string cmd, string[] args)
        {
            if (args.Length == 0) { Monitor.Log("Usage: agent_chat <message>", LogLevel.Info); return; }
            string msg = string.Join(" ", args);
            Monitor.Log($"[You -> Bot]: {msg}", LogLevel.Info);
            // send to LM Studio, get action plan, execute
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string reply = AskLmstudio(
                        $"You are a Stardew Valley bot. Player says: \"{msg}\". " +
                        $"Game state: {GetStateJson()}. Goal: {currentGoal}. " +
                        "Reply with ONLY JSON: {\"action\":\"move|tool|interact|none\", " +
                        "\"direction\":\"up|down|left|right\", \"tiles\":1-3, \"slot\":1-12, \"say\":\"<reply>\"}");
                    Monitor.Log($"[Bot]: {reply}", LogLevel.Info);
                    ExecutePlan(reply);
                }
                catch (Exception ex) { Monitor.Log($"Chat failed: {ex.Message}", LogLevel.Error); }
            });
        }

        private void OnGoalCommand(string cmd, string[] args)
        {
            currentGoal = string.Join(" ", args);
            Monitor.Log($"[Goal set]: {currentGoal}", LogLevel.Info);
        }

        private void OnGotoCommand(string cmd, string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y))
            { Monitor.Log("Usage: agent_goto <x> <y>", LogLevel.Info); return; }
            Monitor.Log($"[Bot]: walking to ({x},{y})", LogLevel.Info);
            ThreadPool.QueueUserWorkItem(_ => WalkTo(x, y));
        }

        private void OnToolCommand(string cmd, string[] args)
        {
            if (args.Length < 1 || !int.TryParse(args[0], out int slot) || slot < 1 || slot > 12)
            { Monitor.Log("Usage: agent_tool <slot 1-12>", LogLevel.Info); return; }
            UseTool(slot - 1);
            Monitor.Log($"[Bot]: used tool in slot {slot}", LogLevel.Info);
        }

        private void OnStopCommand(string cmd, string[] args)
        {
            lock (moveLock) { moveDir = null; moveTilesLeft = 0; }
            currentGoal = "";
            Monitor.Log("[Bot]: stopped", LogLevel.Info);
        }

        private void OnLmstudioCommand(string cmd, string[] args)
        {
            if (args.Length >= 1) lmstudioUrl = args[0];
            if (args.Length >= 2) lmstudioModel = args[1];
            Monitor.Log($"[LM Studio]: {lmstudioUrl} model={lmstudioModel}", LogLevel.Info);
        }

        #endregion

        #region LM Studio brain

        private string AskLmstudio(string prompt)
        {
            var body = JsonSerializer.Serialize(new
            {
                model = lmstudioModel,
                messages = new[] { new { role = "user", content = prompt } },
                max_tokens = 200,
                temperature = 0.3
            });
            var req = (HttpWebRequest)WebRequest.Create(lmstudioUrl + "/v1/chat/completions");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 60000;
            using (var sw = new StreamWriter(req.GetRequestStream()))
                sw.Write(body);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                var doc = JsonDocument.Parse(sr.ReadToEnd());
                return doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString().Trim();
            }
        }

        private void ExecutePlan(string json)
        {
            try
            {
                // strip code fences
                if (json.StartsWith("```"))
                    json = json.Trim('`').Split('\n', 2)[1].Split("```")[0];
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string say = root.TryGetProperty("say", out var se) ? se.GetString() : "";
                if (!string.IsNullOrEmpty(say)) Monitor.Log($"[Bot says]: {say}", LogLevel.Info);
                string action = root.TryGetProperty("action", out var ae) ? ae.GetString() : "none";
                if (action == "move" && root.TryGetProperty("direction", out var de))
                {
                    string dir = de.GetString().ToLower();
                    int tiles = root.TryGetProperty("tiles", out var te) ? Math.Min(10, te.GetInt32()) : 2;
                    lock (moveLock) { moveDir = dir; moveTilesLeft = tiles; }
                }
                else if (action == "tool" && root.TryGetProperty("slot", out var sle))
                {
                    UseTool(Math.Max(0, Math.Min(11, sle.GetInt32() - 1)));
                }
                else if (action == "interact")
                {
                    DoInteract();
                }
            }
            catch (Exception ex) { Monitor.Log($"Plan failed: {ex.Message}", LogLevel.Warn); }
        }

        private void WalkTo(int tx, int ty)
        {
            // simple greedy walk toward target with stuck detection
            int stuck = 0;
            for (int i = 0; i < 200; i++)
            {
                if (!Context.IsWorldReady) break;
                var p = Game1.player;
                int cx = (int)p.Tile.X, cy = (int)p.Tile.Y;
                if (cx == tx && cy == ty) break;
                string dir = Math.Abs(tx - cx) > Math.Abs(ty - cy)
                    ? (tx > cx ? "right" : "left")
                    : (ty > cy ? "down" : "up");
                int ox = cx, oy = cy;
                lock (moveLock) { moveDir = dir; moveTilesLeft = 1; }
                Thread.Sleep(600);
                if ((int)Game1.player.Tile.X == ox && (int)Game1.player.Tile.Y == oy)
                {
                    stuck++;
                    if (stuck >= 3) break;
                    // try perpendicular
                    dir = dir == "up" || dir == "down"
                        ? (tx > cx ? "right" : "left") : (ty > cy ? "down" : "up");
                    lock (moveLock) { moveDir = dir; moveTilesLeft = 1; }
                    Thread.Sleep(600);
                }
                else stuck = 0;
            }
            Monitor.Log($"[Bot]: arrived near ({tx},{ty})", LogLevel.Info);
        }

        #endregion

        #region Actions

        private void UseTool(int slot)
        {
            if (!Context.IsWorldReady) return;
            var p = Game1.player;
            // select hotbar slot
            p.CurrentToolIndex = slot;
            // face and use: simulate tool use on facing tile
            var loc = Game1.currentLocation;
            var tile = p.Tile;
            int fx = (int)tile.X, fy = (int)tile.Y;
            switch (p.FacingDirection)
            {
                case 0: fy--; break; case 1: fx++; break;
                case 2: fy++; break; case 3: fx--; break;
            }
            // use the tool via game logic
            var tool = p.CurrentTool;
            if (tool != null)
            {
                p.canMove = false;
                try
                {
                    p.faceDirection(p.FacingDirection);
                    tool.DoFunction(loc, fx, fy, 1, p);
                }
                finally { p.canMove = true; }
                Monitor.Log($"[Bot]: used {tool.Name} on ({fx},{fy})", LogLevel.Info);
            }
        }

        private void DoInteract()
        {
            if (!Context.IsWorldReady) return;
            var p = Game1.player;
            Game1.currentLocation.checkAction(
                new Location((int)p.Tile.X, (int)p.Tile.Y), Game1.viewport, p);
        }

        #endregion

        #region HTTP server (for external agents)

        private void StartHttp()
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:8933/");
                listener.Start();
                var t = new Thread(() =>
                {
                    while (listener.IsListening)
                    {
                        try
                        {
                            var ctx = listener.GetContext();
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try { Route(ctx); }
                                catch (Exception ex) { Respond(ctx, 500, "{\"error\":\"" + ex.Message + "\"}"); }
                            });
                        }
                        catch { break; }
                    }
                });
                t.IsBackground = true;
                t.Start();
                Monitor.Log("GameAgent HTTP on 127.0.0.1:8933", LogLevel.Info);
            }
            catch (Exception ex) { Monitor.Log($"HTTP failed: {ex.Message}", LogLevel.Error); }
        }

        private void Route(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
            if (ctx.Request.HttpMethod == "GET" && path == "/state")
                Respond(ctx, 200, GetStateJson());
            else if (ctx.Request.HttpMethod == "GET" && path == "/goal")
                Respond(ctx, 200, $"{{\"goal\":\"{currentGoal}\"}}");
            else if (ctx.Request.HttpMethod == "POST" && path == "/move")
            {
                var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                try
                {
                    var doc = JsonDocument.Parse(body);
                    var dir = doc.RootElement.GetProperty("direction").GetString().ToLowerInvariant();
                    var tiles = doc.RootElement.TryGetProperty("tiles", out var te) ? te.GetInt32() : 1;
                    if (dir == "up" || dir == "down" || dir == "left" || dir == "right")
                    {
                        int requested = Math.Max(1, Math.Min(20, tiles));
                        int startTiles;
                        lock (moveLock) { moveDir = dir; moveTilesLeft = requested; startTiles = moveTilesLeft; }
                        int waited = 0;
                        while (waited < 150)
                        {
                            Thread.Sleep(100); waited++;
                            lock (moveLock) { if (moveDir == null) break; }
                            if (!Context.IsWorldReady) break;
                        }
                        int done;
                        lock (moveLock) { done = startTiles - moveTilesLeft; moveDir = null; }
                        var p = Game1.player;
                        Respond(ctx, 200, $"{{\"ok\":true,\"moved\":{done},\"x\":{(int)p.Tile.X},\"y\":{(int)p.Tile.Y}}}");
                    }
                    else Respond(ctx, 400, "{\"error\":\"bad direction\"}");
                }
                catch { Respond(ctx, 400, "{\"error\":\"bad json\"}"); }
            }
            else if (ctx.Request.HttpMethod == "POST" && path == "/tool")
            {
                var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                try
                {
                    var doc = JsonDocument.Parse(body);
                    int slot = doc.RootElement.GetProperty("slot").GetInt32();
                    UseTool(Math.Max(0, Math.Min(11, slot - 1)));
                    Respond(ctx, 200, "{\"ok\":true}");
                }
                catch { Respond(ctx, 400, "{\"error\":\"bad json\"}"); }
            }
            else if (ctx.Request.HttpMethod == "POST" && path == "/interact")
            {
                DoInteract();
                Respond(ctx, 200, "{\"ok\":true}");
            }
            else if (ctx.Request.HttpMethod == "POST" && path == "/goal")
            {
                var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                try
                {
                    var doc = JsonDocument.Parse(body);
                    currentGoal = doc.RootElement.GetProperty("goal").GetString();
                    Respond(ctx, 200, "{\"ok\":true}");
                }
                catch { Respond(ctx, 400, "{\"error\":\"bad json\"}"); }
            }
            else if (ctx.Request.HttpMethod == "POST" && path == "/stop")
            {
                lock (moveLock) { moveDir = null; moveTilesLeft = 0; }
                Respond(ctx, 200, "{\"ok\":true}");
            }
            else Respond(ctx, 404, "{\"error\":\"not found\"}");
        }

        private static void Respond(HttpListenerContext ctx, int code, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private string GetStateJson()
        {
            if (!Context.IsWorldReady) return "{\"ready\":false}";
            var p = Game1.player;
            var loc = Game1.currentLocation;
            var sb = new StringBuilder();
            sb.Append("{\"ready\":true");
            sb.Append($",\"x\":{(int)p.Tile.X},\"y\":{(int)p.Tile.Y}");
            sb.Append($",\"location\":\"{loc.Name}\"");
            sb.Append($",\"time\":{Game1.timeOfDay},\"day\":{Game1.dayOfMonth}");
            sb.Append($",\"facing\":{p.FacingDirection}");
            sb.Append($",\"health\":{p.health},\"energy\":{p.stamina}");
            // facing tile info
            int fx = (int)p.Tile.X, fy = (int)p.Tile.Y;
            switch (p.FacingDirection) { case 0: fy--; break; case 1: fx++; break; case 2: fy++; break; case 3: fx--; break; }
            sb.Append($",\"facing_tile\":{{\"x\":{fx},\"y\":{fy}}}");
            // what's on facing tile?
            string tileInfo = "empty";
            if (loc.terrainFeatures.TryGetValue(new Vector2(fx, fy), out var tf))
            {
                if (tf is HoeDirt hd)
                    tileInfo = hd.crop != null ? (hd.crop.fullyGrown.Value ? "mature_crop" : "growing_crop") : (hd.state.Value == 1 ? "watered_soil" : "tilled_soil");
                else tileInfo = tf.GetType().Name;
            }
            else if (loc.objects.TryGetValue(new Vector2(fx, fy), out var obj))
                tileInfo = obj.Name;
            sb.Append($",\"facing_tile_contents\":\"{tileInfo}\"");
            // current tool
            sb.Append($",\"current_tool\":\"{p.CurrentTool?.Name ?? "none"}\"");
            sb.Append($",\"goal\":\"{currentGoal}\"");
            sb.Append("}");
            return sb.ToString();
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || !e.IsMultipleOf(2)) return;
            string dir;
            lock (moveLock)
            {
                dir = moveDir;
                if (moveTilesLeft <= 0) { moveDir = null; return; }
            }
            if (dir == null) return;
            var p = Game1.player;
            var loc = Game1.currentLocation;
            int dx = 0, dy = 0, facing = 2;
            switch (dir)
            {
                case "up": dy = -1; facing = 0; break;
                case "right": dx = 1; facing = 1; break;
                case "down": dy = 1; facing = 2; break;
                case "left": dx = -1; facing = 3; break;
            }
            p.faceDirection(facing);
            var cur = p.Tile;
            int tx = (int)cur.X + dx, ty = (int)cur.Y + dy;
            var targetPx = new Vector2((tx + 0.5f) * 64f, (ty + 0.5f) * 64f);
            var pos = p.position.Value;
            var diff = targetPx - pos;
            float step = 8f;
            if (diff.Length() <= step + 1f)
            {
                var next = new Vector2((tx + dx + 0.5f) * 64f, (ty + dy + 0.5f) * 64f);
                var box = new Microsoft.Xna.Framework.Rectangle((int)(next.X - 12), (int)(next.Y - 12), 24, 24);
                if (loc.isCollidingPosition(box, Game1.viewport, false, 0, false, p))
                { lock (moveLock) { moveTilesLeft = 0; moveDir = null; } return; }
                p.position.Value = targetPx;
                lock (moveLock) { moveTilesLeft--; if (moveTilesLeft <= 0) moveDir = null; }
            }
            else
            {
                var box = new Microsoft.Xna.Framework.Rectangle(
                    (int)(pos.X + diff.X / diff.Length() * step - 12),
                    (int)(pos.Y + diff.Y / diff.Length() * step - 12), 24, 24);
                if (!loc.isCollidingPosition(box, Game1.viewport, false, 0, false, p))
                    p.position.Value = pos + diff / diff.Length() * step;
                else { lock (moveLock) { moveTilesLeft = 0; moveDir = null; } }
            }
        }

        #endregion
    }
}
