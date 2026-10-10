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
using xTile.Dimensions;

namespace GameAgentMod
{
    /// <summary>HTTP control surface for an external game-playing agent.
    /// GET  /state              -> player/location/time JSON
    /// POST /move   {direction, tiles} -> walk N tiles (up/down/left/right)
    /// POST /interact {}       -> press action button on facing tile
    /// POST /stop   {}         -> cancel movement</summary>
    public class ModEntry : Mod
    {
        private HttpListener listener;
        private string moveDir;
        private int moveTilesLeft;
        private readonly object moveLock = new object();

        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            StartHttp();
        }

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
                Monitor.Log("GameAgent HTTP listening on 127.0.0.1:8933", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"HTTP start failed: {ex.Message}", LogLevel.Error);
            }
        }

        private void Route(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
            if (ctx.Request.HttpMethod == "GET" && path == "/state")
                Respond(ctx, 200, GetStateJson());
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
                        lock (moveLock) { moveDir = dir; moveTilesLeft = Math.Max(1, Math.Min(60, tiles)); }
                        Respond(ctx, 200, "{\"ok\":true}");
                    }
                    else Respond(ctx, 400, "{\"error\":\"bad direction\"}");
                }
                catch { Respond(ctx, 400, "{\"error\":\"bad json\"}"); }
            }
            else if (ctx.Request.HttpMethod == "POST" && path == "/interact")
            {
                if (Context.IsWorldReady)
                {
                    var p = Game1.player;
                    Game1.currentLocation.checkAction(
                        new Location((int)p.Tile.X, (int)p.Tile.Y),
                        Game1.viewport, p);
                }
                Respond(ctx, 200, "{\"ok\":true}");
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
            sb.Append($",\"moving\":{(moveDir != null).ToString().ToLower()}");
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
            int dx = 0, dy = 0;
            int facing = 2;
            switch (dir)
            {
                case "up": dy = -1; facing = 0; break;
                case "right": dx = 1; facing = 1; break;
                case "down": dy = 1; facing = 2; break;
                case "left": dx = -1; facing = 3; break;
            }
            p.faceDirection(facing);

            // target tile center in pixels
            var cur = p.Tile;
            int tx = (int)cur.X + dx, ty = (int)cur.Y + dy;
            var targetPx = new Vector2((tx + 0.5f) * 64f, (ty + 0.5f) * 64f);
            var pos = p.position.Value;
            var diff = targetPx - pos;
            float step = 8f; // px per tick (~half speed, reliable)
            if (diff.Length() <= step + 1f)
            {
                // arrived at tile center; check next tile passable
                var next = new Vector2((tx + dx + 0.5f) * 64f, (ty + dy + 0.5f) * 64f);
                var box = new Microsoft.Xna.Framework.Rectangle((int)(next.X - 12), (int)(next.Y - 12), 24, 24);
                if (loc.isCollidingPosition(box, Game1.viewport, false, 0, false, p))
                {
                    lock (moveLock) { moveTilesLeft = 0; moveDir = null; }
                    return; // blocked
                }
                p.position.Value = targetPx;
                lock (moveLock)
                {
                    moveTilesLeft--;
                    if (moveTilesLeft <= 0) moveDir = null;
                }
            }
            else
            {
                var box = new Microsoft.Xna.Framework.Rectangle((int)(pos.X + diff.X / diff.Length() * step - 12),
                                        (int)(pos.Y + diff.Y / diff.Length() * step - 12), 24, 24);
                if (!loc.isCollidingPosition(box, Game1.viewport, false, 0, false, p))
                    p.position.Value = pos + diff / diff.Length() * step;
                else
                {
                    lock (moveLock) { moveTilesLeft = 0; moveDir = null; }
                }
            }
        }
    }
}
