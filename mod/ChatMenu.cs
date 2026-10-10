using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace GameAgentMod
{
    /// <summary>In-game chat UI for the GameAgent bot. Stardew-themed.
    /// Press T to open. Type a message, hit Enter to send to the bot.</summary>
    public class ChatMenu : IClickableMenu
    {
        private readonly ModEntry mod;
        private string inputText = "";
        private readonly List<string> messages = new List<string>();
        private TextBox textBox;
        private ClickableComponent sendButton;
        private ClickableComponent closeButton;
        private ClickableComponent goalButton;

        private const int Width = 600;
        private const int Height = 400;

        public ChatMenu(ModEntry mod) : base(
            (Game1.viewport.Width - Width) / 2,
            (Game1.viewport.Height - Height) / 2,
            Width, Height, true)
        {
            this.mod = mod;
            int bx = xPositionOnScreen + 20;
            int by = yPositionOnScreen + Height - 60;

            textBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null,
                Game1.smallFont, Game1.textColor)
            {
                X = bx,
                Y = by,
                Width = Width - 140,
                Height = 40,
            };
            textBox.OnEnterPressed += _ => SendMessage();
            textBox.Selected = true;

            sendButton = new ClickableComponent(
                new Rectangle(bx + Width - 110, by, 90, 40), "Send");
            closeButton = new ClickableComponent(
                new Rectangle(xPositionOnScreen + Width - 50, yPositionOnScreen + 10, 40, 40), "X");
            goalButton = new ClickableComponent(
                new Rectangle(bx, yPositionOnScreen + 10, 120, 35), "Set Goal");
        }

        public void AddMessage(string from, string text)
        {
            messages.Add($"[{from}]: {text}");
            if (messages.Count > 8) messages.RemoveAt(0);
        }

        private void SendMessage()
        {
            if (string.IsNullOrWhiteSpace(inputText)) return;
            string msg = inputText;
            inputText = "";
            textBox.Text = "";
            AddMessage("You", msg);
            mod.HandleChat(msg, reply =>
            {
                if (Game1.activeClickableMenu is ChatMenu cm)
                    cm.AddMessage("Bot", reply);
            });
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);
            textBox.Update();
            if (sendButton.containsPoint(x, y))
            {
                SendMessage();
                Game1.playSound("coin");
            }
            else if (closeButton.containsPoint(x, y))
            {
                Game1.activeClickableMenu = null;
                Game1.playSound("bigDeSelect");
            }
            else if (goalButton.containsPoint(x, y))
            {
                // quick goal: use last message as goal
                if (messages.Count > 0)
                {
                    string last = messages[messages.Count - 1];
                    mod.SetGoal("from chat: " + last);
                    AddMessage("System", "Goal set from last message");
                }
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.activeClickableMenu = null;
                return;
            }
            // let textbox handle typing
            if (key == Keys.Enter)
            {
                SendMessage();
                return;
            }
            base.receiveKeyPress(key);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            textBox.Update();
            inputText = textBox.Text;
        }

        public override void draw(SpriteBatch b)
        {
            // dim background
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(0, 0, Game1.viewport.Width, Game1.viewport.Height),
                Color.Black * 0.5f);

            // wooden dialogue box (Stardew theme)
            IClickableMenu.drawTextureBox(b,
                Game1.mouseCursors, new Rectangle(384, 396, 15, 15),
                xPositionOnScreen, yPositionOnScreen, Width, Height,
                Color.White, 4f, false);

            // title
            string title = "🦆 DuckBot Chat";
            Vector2 tSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (Width - tSize.X) / 2, yPositionOnScreen + 15),
                Game1.textColor);

            // messages
            float my = yPositionOnScreen + 60;
            foreach (var m in messages)
            {
                // wrap long messages
                string wrapped = Game1.parseText(m, Game1.smallFont, Width - 60);
                b.DrawString(Game1.smallFont, wrapped,
                    new Vector2(xPositionOnScreen + 30, my), Game1.textColor);
                my += Game1.smallFont.MeasureString(wrapped).Y + 8;
            }

            // goal display
            string goal = mod.GetGoal();
            if (!string.IsNullOrEmpty(goal))
            {
                b.DrawString(Game1.smallFont, $"🎯 Goal: {goal}",
                    new Vector2(xPositionOnScreen + 30, yPositionOnScreen + Height - 90),
                    Color.DarkGreen);
            }

            // text input
            textBox.Draw(b);

            // buttons (Stardew-style)
            DrawButton(b, sendButton, "Send");
            DrawButton(b, closeButton, "X");
            DrawButton(b, goalButton, "Goal");

            base.draw(b);
            drawMouse(b);
        }

        private void DrawButton(SpriteBatch b, ClickableComponent btn, string label)
        {
            IClickableMenu.drawTextureBox(b,
                Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                btn.bounds.X, btn.bounds.Y, btn.bounds.Width, btn.bounds.Height,
                Color.White, 4f, false);
            Vector2 s = Game1.smallFont.MeasureString(label);
            b.DrawString(Game1.smallFont, label,
                new Vector2(btn.bounds.X + (btn.bounds.Width - s.X) / 2,
                            btn.bounds.Y + (btn.bounds.Height - s.Y) / 2),
                Game1.textColor);
        }
    }
}
