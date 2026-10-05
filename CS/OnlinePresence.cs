using System;
using System.Collections.Generic;
using System.Text;
using ConsoleLib.Console;
using XRL;
using XRL.Core;
using XRL.World;

namespace QudOnline
{
    // Who else is online and where. While connected, this game tells the hub its player's zone and cell whenever
    // they change, and keeps a table of what the other games report. A player standing in the zone you are in is
    // drawn as an '@' on top of the map: a drawing only, not an object in the zone. Nothing here is saved.
    public static class OnlinePresence
    {
        public class Other
        {
            public string ID;
            public string Name;
            public string ZoneID;
            public int X;
            public int Y;

            // Hit points and their maximum; -1 when not reported.
            public int HP = -1;
            public int MaxHP = -1;
        }

        public static readonly Dictionary<string, Other> Others = new Dictionary<string, Other>();

        private const char MarkerColor = 'M';

        // The game's callbacks cannot be taken back, so they are registered once and stay.
        private static bool Hooked;
        private static bool ReportBroken;
        private static bool DrawBroken;

        // What the hub was last told.
        private static string SentID;
        private static string SentName;
        private static string SentZone;
        private static int SentX;
        private static int SentY;
        private static int SentHP;

        // A connection was made: start reporting, starting with where the player is now.
        public static void Start()
        {
            if (!Hooked)
            {
                Hooked = true;
                OnlineLink.Received = Receive;
                OnlineLink.Lost = Forget;
                XRLCore.RegisterOnBeginPlayerTurnCallback(BeginTurn);
                XRLCore.RegisterOnEndPlayerTurnCallback(Tick);
                XRLCore.RegisterAfterRenderCallback(OnRender);
            }
            Forget();
            OnlineBackground.Start();
            ReportBroken = false;
            DrawBroken = false;
            Report();
        }

        // The connection is gone: nobody is known any more, and the next connection is told everything again.
        public static void Forget()
        {
            Others.Clear();
            OnlineAvatars.Clear();
            SentID = null;
            OnlineWorld.Forget();
        }

        // Whether the hub was told this game's player is somewhere in the world.
        public static bool InWorld
        {
            get
            {
                return SentID != null && !string.IsNullOrEmpty(SentZone);
            }
        }

        // The game this player was reported for has ended (death, or back to the main menu): the others are told
        // the player is nowhere, and the hub that they hold no zone. Called from the background thread.
        public static void LeaveWorld()
        {
            string id = SentID;
            string name = SentName;
            if (id == null)
            {
                return;
            }
            SentID = null;
            SentZone = null;
            OnlineLink.Send(OnlineLink.Here,
                Encoding.UTF8.GetBytes(id + "\t" + (name ?? "").Replace('\t', ' ') + "\t\t0\t0"));
            OnlineLink.Send(OnlineLink.ClaimZone, Encoding.UTF8.GetBytes(id + "\t"));
            OnlineWorld.LeftWorld = true;
            MetricsManager.LogInfo("[QUDOnline] The game has ended: left the hub's world.");
        }

        // Runs on the game thread once at the start of each turn of the player, after the creatures have acted.
        private static void BeginTurn(XRLCore Core)
        {
            OnlineWorld.BeginTurn();
        }

        // Runs on the game thread many times a second while the game waits for a key.
        private static void Tick(XRLCore Core)
        {
            // The world goes first: a zone that was left is stored and the new one claimed before the others hear
            // of the move.
            OnlineWorld.Tick();
            if (ReportBroken)
            {
                return;
            }
            try
            {
                Report();
            }
            catch (Exception x)
            {
                // Never let a bug here repeat on every pass.
                ReportBroken = true;
                OnlineLog.Error("OnlinePresence.Report", x);
            }
        }

        private static void Report()
        {
            if (!OnlineLink.Connected || The.Game == null)
            {
                return;
            }
            Cell cell = The.Player?.CurrentCell;
            if (cell?.ParentZone == null)
            {
                return;
            }
            string id = The.Game.GameID;
            string name = The.Game.PlayerName;
            string zone = cell.ParentZone.ZoneID;
            int hp = The.Player.hitpoints;
            if (id == SentID && name == SentName && zone == SentZone && cell.X == SentX && cell.Y == SentY
                && hp == SentHP)
            {
                return;
            }
            SentID = id;
            SentName = name;
            SentZone = zone;
            SentX = cell.X;
            SentY = cell.Y;
            SentHP = hp;
            string line = id + "\t" + (name ?? "").Replace('\t', ' ') + "\t" + zone + "\t" + cell.X + "\t" + cell.Y
                + "\t" + hp + "\t" + The.Player.GetStat("Hitpoints")?.BaseValue;
            OnlineLink.Send(OnlineLink.Here, Encoding.UTF8.GetBytes(line));
        }

        private static void Receive(byte Type, byte[] Payload)
        {
            if (Type == OnlineLink.Reply || Type == OnlineLink.Failure)
            {
                // The answer to something sent without waiting for one.
                return;
            }
            if (Type == OnlineLink.Tell)
            {
                OnlineWorld.Told(Payload);
                return;
            }
            if (Type == OnlineLink.Mirror)
            {
                OnlineWorld.Mirrored(Payload);
                return;
            }
            if (Type == OnlineLink.Body)
            {
                OnlineAvatars.Received(Payload);
                return;
            }
            if (Type == OnlineLink.Act)
            {
                OnlineWorld.Acted(Payload);
                return;
            }
            string text = Encoding.UTF8.GetString(Payload);
            if (Type == OnlineLink.Role)
            {
                OnlineWorld.Role(text);
                return;
            }
            if (Type == OnlineLink.Wait)
            {
                OnlineWorld.Waited(text);
                return;
            }
            if (Type == OnlineLink.Want)
            {
                OnlineWorld.Want(text);
                return;
            }
            string here = The.Player?.CurrentZone?.ZoneID;
            if (Type == OnlineLink.Gone)
            {
                if (Others.TryGetValue(text, out Other left))
                {
                    Others.Remove(text);
                    OnlineAvatars.Sync();
                    OnlineLog.Log(left.Name + " went offline.");
                }
                return;
            }
            if (Type == OnlineLink.Changed)
            {
                OnlineWorld.Changed(text);
                return;
            }
            if (Type != OnlineLink.Here)
            {
                OnlineLog.Log("Unknown message of type " + Type + ", " + Payload.Length + " bytes.");
                return;
            }
            string[] fields = text.Split('\t');
            if (fields.Length < 5 || !int.TryParse(fields[3], out int x) || !int.TryParse(fields[4], out int y))
            {
                return;
            }
            if (fields[0] == The.Game?.GameID)
            {
                return;
            }
            if (!Others.TryGetValue(fields[0], out Other other))
            {
                other = new Other { ID = fields[0] };
                Others[other.ID] = other;
                OnlineLog.Log(fields[1] + " is online, in " + (fields[2] == here ? "your zone." : fields[2] + "."));
            }
            else if (other.ZoneID != fields[2])
            {
                if (fields[2] == here)
                {
                    OnlineLog.Log(fields[1] + " enters your zone.");
                }
                else if (other.ZoneID == here)
                {
                    OnlineLog.Log(fields[1] + " leaves your zone.");
                }
            }
            other.Name = fields[1];
            other.ZoneID = fields[2];
            if (fields.Length >= 7 && int.TryParse(fields[5], out int hp) && int.TryParse(fields[6], out int maxHP))
            {
                other.HP = hp;
                other.MaxHP = maxHP;
            }
            other.X = x;
            other.Y = y;
            // The avatar follows at once, not at the next idle pass.
            OnlineAvatars.Sync();
        }

        // Drawn straight onto the screen after the map is rendered, as DomainModQud's DomainSpread does.
        private static void OnRender(XRLCore Core, ScreenBuffer Buffer)
        {
            if (DrawBroken || Others.Count == 0 || Buffer == null)
            {
                return;
            }
            try
            {
                string zone = The.ZoneManager?.ActiveZone?.ZoneID;
                if (zone == null)
                {
                    return;
                }
                foreach (Other other in Others.Values)
                {
                    // The marker is only for a player whose body has not arrived.
                    if (OnlineAvatars.Has(other.ID) || other.ZoneID != zone || other.X < 0 || other.Y < 0
                        || other.X >= Buffer.Width || other.Y >= Buffer.Height)
                    {
                        continue;
                    }
                    ConsoleChar c = Buffer[other.X, other.Y];
                    c.Clear();
                    c.Char = '@';
                    c.SetForeground(MarkerColor);
                    c.SetBackground('k');
                    ScreenBuffer.ImposterSuppression[other.X, other.Y] = true;
                }
            }
            catch (Exception x)
            {
                // Never let a drawing bug repeat every frame.
                DrawBroken = true;
                OnlineLog.Error("OnlinePresence.OnRender", x);
            }
        }
    }
}
