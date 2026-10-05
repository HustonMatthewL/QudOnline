using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using XRL;
using XRL.Core;
using XRL.World;

namespace QudOnline
{
    // The shared world. While connected, the hub's copy of a zone is the true one, and a zone with players in it
    // has one owner (the first to arrive, decided by the hub):
    //   - the owner's game simulates the zone and is the only one that stores it: when a guest is about to enter,
    //     after its turns while guests are present, when it leaves and when the game is saved;
    //   - a guest's copy is a mirror: its creatures do not act, each store of the owner replaces it, and it is
    //     never stored. What a guest drops or takes, and the damage it does to creatures, is sent to the owner,
    //     who does the same to the real zone;
    //   - a zone the game asks for and does not have in memory is fetched from the hub; when the hub has none, the
    //     game builds it as usual and the hub keeps that first version;
    //   - a zone in memory that another player has stored since is marked stale and fetched again when next asked for.
    // Only zones of the main world are shared; the world map, interiors and pocket worlds stay local.
    // The instance is a game system so it can answer GetZoneEvent, which vanilla ZoneManager.GetZone raises before it
    // looks in its cache, its frozen zones or the generator. It has no fields: everything here is forgotten on
    // disconnecting and nothing is saved.
    [Serializable]
    public class OnlineWorld : IGameSystem
    {
        // Game state holding the seed of the hub's world this game was made for.
        public const string SeedState = "QUDOnline.Seed";

        private const string SharedWorld = "JoppaWorld.";
        private const int HubTimeout = 5000;

        // The owner's stores of a zone for its guests are kept at least this far apart.
        private const int MirrorInterval = 100;

        // An object taken out of its cell's list without events, and where it was.
        private struct Held
        {
            public GameObject Object;
            public int X;
            public int Y;
            public int Index;
        }

        // Zones in memory that the hub has a newer version of.
        private static readonly HashSet<string> Stale = new HashSet<string>();

        // Zones this class is fetching or building right now: the game's own lookup must run for them.
        private static readonly HashSet<string> Busy = new HashSet<string>();

        private static bool Broken;

        // The game the zones in memory were last compared with the hub for.
        private static XRLGame SeenGame;

        // The zone last claimed on the hub, and what the hub said about it.
        private static string LastZone;
        private static string RoleZone;
        private static string Owner;
        private static int Players;

        // Owner: whether the guests are owed a newer copy of the zone, and how long ago the last one went out.
        private static bool MirrorDue = true;
        private static readonly Stopwatch SinceSent = Stopwatch.StartNew();

        // Guest: the owner's latest store, waiting for the idle pass, and the turn creatures were last silenced.
        private static byte[] Mirror;
        private static string MirrorZone;
        private static long QuietTurn = -1;

        // Guest: the objects in the mirror's cells that came from the owner. Anything else lying there was put
        // down by this player and has to be sent to the owner.
        private static readonly HashSet<GameObject> Known = new HashSet<GameObject>();
        private static string KnownZone;
        private static long GiveTurn = -1;

        // The hub made this guest the owner; the last update of the old owner is applied first, at the idle pass.
        private static bool Promoted;

        // The player object the system listens to for things taken.
        private static GameObject HookedPlayer;

        private const byte ActGive = 1;
        private const byte ActTake = 2;
        private const byte ActHit = 3;
        private const byte ActBreak = 4;

        // What is remembered of something in the mirror that has hit points. A creature is named to the owner by
        // its ID; anything else (a wall, a door, a plant) by its cell and blueprint, since it does not move.
        private class Noted
        {
            public int ID;
            public int X;
            public int Y;
            public string Blueprint;
            public int HP;
        }

        // Guest: everything in the mirror that has hit points, as the owner last sent it. Nothing in a mirror
        // acts, so whatever has been lost since was taken by this player.
        private static readonly Dictionary<GameObject, Noted> Health = new Dictionary<GameObject, Noted>();

        // Set by the background thread when the game ended and the hub was told the player is nowhere: the next
        // game starts from scratch.
        public static volatile bool LeftWorld;

        private static int HurtsTaken;

        // Guest: how many of each blueprint the player carried when the mirror's cells were last looked at. An
        // item that appears on the ground is the player's only if that many fewer are carried now.
        private static Dictionary<string, int> Carried = new Dictionary<string, int>();

        private static int HitsSent;
        private static int HitsApplied;

        // The zone the others were last sent this player's body for, what the body looked like, and whether a
        // turn has passed since that was last checked.
        private static string BodyZone;
        private static string BodyPrint;
        private static bool BodyCheck;

        // Rounds: whether the player's current turn has not been reported to the hub yet; how many actions the
        // player took in shared zones, for how many of them a round was waited for, and how long that took.
        private const int RoundTimeout = 2000;
        public static bool RoundsOn = true;
        private static bool TurnOpen;
        private static int Actions;
        private static int Rounds;
        private static long RoundTotal;
        private static long RoundWorst;

        // Rounds: the player's current turn was ended by the hub's Wait, and how many waits were applied or came
        // when there was no turn to spend.
        private static bool Forced;
        private static int WaitsApplied;
        private static int WaitsIgnored;

        // Owner: the last snapshot stored for the guests, to measure how small the difference to the next one is.
        private static byte[] LastRaw;
        private static string LastRawZone;
        private static bool DeltaBroken;
        private static int DeltaCount;
        private static int DeltaFailed;
        private static long DeltaWhole;
        private static long DeltaSize;
        private static long DeltaMakeTime;
        private static long DeltaApplyTime;

        public static bool MirrorOn = true;
        private static int MirrorCount;
        private static long MirrorTotal;
        private static long MirrorWorst;

        public override void Register(XRLGame Game, IEventRegistrar Registrar)
        {
            Registrar.Register(GetZoneEvent.ID);
        }

        public override bool HandleEvent(GetZoneEvent E)
        {
            try
            {
                Zone zone = Supply(E.ZoneID);
                if (zone != null)
                {
                    E.Result = zone;
                }
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineWorld.Supply " + E.ZoneID, x);
            }
            return base.HandleEvent(E);
        }

        // The player took something. As a guest, the owner has to take it out of the real zone.
        public override bool HandleEvent(TookEvent E)
        {
            try
            {
                if (E.Actor != null && E.Actor.IsPlayer() && E.Item != null)
                {
                    Took(E.Item);
                }
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineWorld.Took", x);
            }
            return base.HandleEvent(E);
        }

        // The save should not hold a newer version of the zone the player stands in than the hub does.
        public override void BeforeSave()
        {
            try
            {
                // Avatars must not end up in the save; they are built again at the next idle pass.
                OnlineAvatars.Clear();
                Zone here = The.Player?.CurrentZone;
                if (!Broken && SeenGame != null && SeenGame == The.Game && OnlineLink.Connected && here != null
                    && here == The.ZoneManager.ActiveZone && Shared(here.ZoneID) && !Guest(here.ZoneID) && !Promoted)
                {
                    StoreActive(here);
                }
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineWorld.BeforeSave", x);
            }
        }

        // A connection was made: at the next pass the zones in memory are compared with the hub.
        public static void Start()
        {
            Forget();
            Broken = false;
        }

        public static void Forget()
        {
            Stale.Clear();
            SeenGame = null;
            LastZone = null;
            RoleZone = null;
            Owner = null;
            Players = 0;
            Mirror = null;
            MirrorZone = null;
            Promoted = false;
            Known.Clear();
            KnownZone = null;
            BodyZone = null;
            OnlineAvatars.Forget();
        }

        public static bool Shared(string ZoneID)
        {
            return ZoneID != null && ZoneID.StartsWith(SharedWorld, StringComparison.Ordinal);
        }

        // Whether another player owns the zone.
        private static bool Guest(string ZoneID)
        {
            return RoleZone == ZoneID && !string.IsNullOrEmpty(Owner) && Owner != The.Game.GameID;
        }

        private static bool Owning(string ZoneID)
        {
            return RoleZone == ZoneID && Owner == The.Game.GameID;
        }

        public static string Describe()
        {
            return "Out of date in memory: " + (Stale.Count == 0 ? "none" : string.Join(", ", Stale)) + ".";
        }

        public static string DescribeRole()
        {
            string here = The.Player?.CurrentZone?.ZoneID;
            string text;
            if (!Shared(here))
            {
                text = "This zone is not shared.";
            }
            else if (RoleZone != here || string.IsNullOrEmpty(Owner))
            {
                text = "The hub has not said who owns " + here + ".";
            }
            else if (Owning(here))
            {
                text = "You own " + here + "; " + Players + " player(s) in it.";
            }
            else
            {
                string name = OnlinePresence.Others.TryGetValue(Owner, out OnlinePresence.Other other)
                    ? other.Name : Owner;
                text = "You are a guest in " + here + ", owned by " + name + "; " + Players + " player(s) in it.";
            }
            text += " Mirror updates are " + (MirrorOn ? "on" : "OFF") + ": " + MirrorCount + " applied";
            if (MirrorCount > 0)
            {
                text += ", " + (MirrorTotal / MirrorCount) + " ms on average, " + MirrorWorst + " ms at worst";
            }
            text += ". Rounds are " + (RoundsOn ? "on" : "OFF") + ": " + Actions
                + " action(s) in shared zones, a round waited for on " + Rounds;
            if (Rounds > 0)
            {
                text += ", " + (RoundTotal / Rounds) + " ms on average, " + RoundWorst + " ms at worst";
            }
            return text + ". Waits applied: " + WaitsApplied + ", ignored: " + WaitsIgnored
                + ". Hits sent to an owner: " + HitsSent + ", applied for guests: " + HitsApplied
                + ". Hits on other players passed on: " + OnlineAvatars.HurtsForwarded
                + ", taken from others' games: " + HurtsTaken + "."
                + (DeltaCount == 0 ? " No differences measured yet (only the owner of a zone with guests measures)."
                    : " Differences measured: " + DeltaCount + "; whole zone " + (DeltaWhole / DeltaCount)
                        + " bytes packed on average, difference "
                        + (DeltaSize / DeltaCount) + "; made in " + (DeltaMakeTime / DeltaCount) + " ms, applied in "
                        + (DeltaApplyTime / DeltaCount)
                        + " ms on average; failed checks: " + DeltaFailed + ".");
        }

        // ------------------------------------------------------------------ messages from the hub

        // A round closed in the zone and this player did not act in it: they spend a turn like everyone else, as
        // the wait command would. Without a turn to spend (the game is busy, or a wait is already pending while a
        // menu is open) nothing happens, so waits never pile up.
        public static void Waited(string ZoneID)
        {
            try
            {
                GameObject player = The.Player;
                if (Broken || !RoundsOn || player == null || SeenGame != The.Game
                    || player.CurrentZone?.ZoneID != ZoneID)
                {
                    return;
                }
                if (player.Energy == null || player.Energy.Value < 1000)
                {
                    WaitsIgnored++;
                    return;
                }
                Forced = true;
                WaitsApplied++;
                player.PassTurn();
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineWorld.Waited", x);
            }
        }

        // Another player stored a zone this player is not in.
        public static void Changed(string ZoneID)
        {
            if (The.ZoneManager != null && The.ZoneManager.CachedZones.ContainsKey(ZoneID))
            {
                Stale.Add(ZoneID);
            }
        }

        // The hub says who owns a zone this player is in: "zone, owner, number of players".
        public static void Role(string Text)
        {
            string[] fields = Text.Split('\t');
            if (fields.Length != 3 || The.Game == null)
            {
                return;
            }
            int.TryParse(fields[2], out int players);
            SetRole(fields[0], fields[1], players);
        }

        // Another player is about to enter a zone this player owns: the hub wants it as it is now.
        public static void Want(string ZoneID)
        {
            if (Broken || The.Game == null || SeenGame != The.Game)
            {
                return;
            }
            Zone here = The.Player?.CurrentZone;
            if (here != null && here.ZoneID == ZoneID && here == The.ZoneManager.ActiveZone && !Guest(ZoneID)
                && !Promoted)
            {
                StoreActive(here);
            }
        }

        // A guest in a zone this player owns dropped or took something. Payload: key length (2 bytes), key, then
        // the kind of act and its details.
        public static void Acted(byte[] Payload)
        {
            try
            {
                if (Broken || The.Game == null || SeenGame != The.Game || Payload.Length < 3)
                {
                    return;
                }
                int length = Payload[0] | (Payload[1] << 8);
                int at = 2 + length;
                if (Payload.Length <= at)
                {
                    return;
                }
                string id = Encoding.UTF8.GetString(Payload, 2, length);
                Zone here = The.Player?.CurrentZone;
                if (here == null || here.ZoneID != id || here != The.ZoneManager.ActiveZone || !Owning(id))
                {
                    MetricsManager.LogInfo("[QUDOnline] A guest's act in " + id + " came too late and was dropped.");
                    return;
                }
                if (Payload[at] == ActGive && Payload.Length > at + 3)
                {
                    byte[] data = new byte[Payload.Length - at - 3];
                    Buffer.BlockCopy(Payload, at + 3, data, 0, data.Length);
                    GameObject given = ZoneSnapshot.ReadObject(data, out int errors);
                    Cell cell = here.GetCell(Payload[at + 1], Payload[at + 2]);
                    if (given != null && cell != null)
                    {
                        if (given.Physics != null)
                        {
                            given.Physics._CurrentCell = null;
                        }
                        cell.AddObject(given);
                        MetricsManager.LogInfo("[QUDOnline] A guest put down " + given.Blueprint
                            + " at " + cell.X + "," + cell.Y + ", read errors " + errors + ".");
                    }
                }
                else if (Payload[at] == ActHit)
                {
                    string[] fields = Encoding.UTF8.GetString(Payload, at + 1, Payload.Length - at - 1).Split('\t');
                    if (fields.Length == 4 && int.TryParse(fields[1], out int creature)
                        && int.TryParse(fields[2], out int damage))
                    {
                        Hit(here, fields[0], creature, damage, fields[3] == "1");
                    }
                }
                else if (Payload[at] == ActBreak)
                {
                    string[] fields = Encoding.UTF8.GetString(Payload, at + 1, Payload.Length - at - 1).Split('\t');
                    if (fields.Length == 6 && int.TryParse(fields[1], out int bx) && int.TryParse(fields[2], out int by)
                        && int.TryParse(fields[4], out int damage))
                    {
                        Break(here, fields[0], bx, by, fields[3], damage, fields[5] == "1");
                    }
                }
                else if (Payload[at] == ActTake)
                {
                    string[] fields = Encoding.UTF8.GetString(Payload, at + 1, Payload.Length - at - 1).Split('\t');
                    if (fields.Length == 5 && int.TryParse(fields[0], out int x) && int.TryParse(fields[1], out int y)
                        && int.TryParse(fields[2], out int baseID) && int.TryParse(fields[4], out int count))
                    {
                        bool found = TakeAway(here, x, y, baseID, fields[3], count);
                        MetricsManager.LogInfo("[QUDOnline] A guest took " + count + " " + fields[3]
                            + " near " + x + "," + y + (found ? "." : ": NOT FOUND here."));
                    }
                }
                // The guests should see the result soon.
                MirrorDue = true;
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineWorld.Acted", x);
            }
        }

        // A guest hurt or killed a creature of its mirror: the real one takes the same from that guest's avatar,
        // through the game's own damage routine, so it turns on the avatar, dies and drops its things as usual.
        private static void Hit(Zone Here, string PlayerID, int CreatureID, int Damage, bool Killed)
        {
            GameObject target = null;
            Here.ForeachObjectWithPart("Brain", delegate(GameObject o)
            {
                if (o._BaseID == CreatureID)
                {
                    target = o;
                }
            });
            if (target == null || CreatureID <= 0)
            {
                MetricsManager.LogInfo("[QUDOnline] A guest hit creature " + CreatureID + ", which is NOT here.");
                return;
            }
            Apply(target, PlayerID, Damage, Killed);
        }

        // The same for something that is not a creature: a wall, a door, a plant. It is found by cell and blueprint.
        private static void Break(Zone Here, string PlayerID, int X, int Y, string Blueprint, int Damage,
            bool Destroyed)
        {
            Cell cell = Here.GetCell(X, Y);
            GameObject target = null;
            if (cell != null)
            {
                foreach (GameObject lying in cell.Objects)
                {
                    if (lying.Blueprint == Blueprint && lying.Brain == null && lying.HasHitpoints())
                    {
                        target = lying;
                        break;
                    }
                }
            }
            if (target == null)
            {
                MetricsManager.LogInfo("[QUDOnline] A guest damaged " + Blueprint + " at " + X + "," + Y
                    + ", which is NOT there.");
                return;
            }
            Apply(target, PlayerID, Damage, Destroyed);
        }

        private static void Apply(GameObject Target, string PlayerID, int Damage, bool Killed)
        {
            GameObject attacker = OnlineAvatars.Get(PlayerID);
            string name = Target.Blueprint;
            int amount = Killed ? Math.Max(Damage, Target.hitpoints) : Damage;
            if (amount > 0)
            {
                Target.TakeDamage(amount, "from %t attack.", Owner: attacker, Attacker: attacker);
            }
            if (Killed && GameObject.Validate(Target) && !Target.IsInGraveyard() && Target.hitpoints > 0)
            {
                Target.Die(attacker);
            }
            HitsApplied++;
            MetricsManager.LogInfo("[QUDOnline] A guest " + (Killed ? "destroyed " : "hit ") + name
                + " for " + amount + ".");
        }

        // Whether this player owns the zone they stand in (or is alone in it), so that what happens here is real.
        public static bool OwnsHere()
        {
            string here = The.Player?.CurrentZone?.ZoneID;
            return here != null && !Guest(here) && !Promoted;
        }

        // Another game says this player's character was hit there: by a creature of the zone's owner, by the
        // environment, or by another player. Payload: ID length (1 byte), this game's ID, then "sender, kind of
        // attacker, attacker's ID, attacker's name, amount, damage types". The character's own resistances apply.
        public static void Told(byte[] Payload)
        {
            try
            {
                GameObject player = The.Player;
                if (Broken || player == null || The.Game == null || !The.Game.Running || Payload.Length < 2)
                {
                    return;
                }
                int at = 1 + Payload[0];
                if (Payload.Length <= at)
                {
                    return;
                }
                string[] fields = Encoding.UTF8.GetString(Payload, at, Payload.Length - at).Split('\t');
                if (fields.Length != 6 || !int.TryParse(fields[2], out int attackerID)
                    || !int.TryParse(fields[4], out int amount) || amount <= 0)
                {
                    return;
                }
                GameObject attacker = null;
                if (fields[1] == "player")
                {
                    attacker = OnlineAvatars.Get(fields[0]);
                }
                else if (fields[1] == "creature" && attackerID > 0)
                {
                    player.CurrentZone?.ForeachObjectWithPart("Brain", delegate(GameObject o)
                    {
                        if (o._BaseID == attackerID)
                        {
                            attacker = o;
                        }
                    });
                }
                string name = fields[3];
                HurtsTaken++;
                MetricsManager.LogInfo("[QUDOnline] Hit for " + amount + " by " + name + " in another player's game.");
                player.TakeDamage(amount, "from " + name + "!", fields[5].Length > 0 ? fields[5] : null,
                    "You were killed by " + name + ".", null, attacker, attacker);
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineWorld.Told", x);
            }
        }

        // Removes what a guest took from the real zone: an object lying within one cell of the guest, or held by
        // something there (a chest, a merchant). Matched by ID when the object has one, else by blueprint.
        private static bool TakeAway(Zone Here, int X, int Y, int BaseID, string Blueprint, int Count)
        {
            List<GameObject> candidates = new List<GameObject>();
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    Cell cell = Here.GetCell(X + dx, Y + dy);
                    if (cell == null)
                    {
                        continue;
                    }
                    foreach (GameObject lying in cell.Objects)
                    {
                        if (lying.IsPlayer())
                        {
                            continue;
                        }
                        if (lying.Takeable)
                        {
                            candidates.Add(lying);
                        }
                        if (lying.Inventory != null)
                        {
                            candidates.AddRange(lying.Inventory.Objects);
                        }
                    }
                }
            }
            GameObject match = null;
            if (BaseID > 0)
            {
                match = candidates.Find((GameObject o) => o._BaseID == BaseID);
            }
            if (match == null)
            {
                match = candidates.Find((GameObject o) => o.Blueprint == Blueprint);
            }
            if (match == null)
            {
                return false;
            }
            if (match.Count > Count)
            {
                match.Count -= Count;
            }
            else
            {
                match.Obliterate();
            }
            return true;
        }

        // The owner of the zone this player is a guest in stored it. Payload: key length (2 bytes), key, packed zone.
        public static void Mirrored(byte[] Payload)
        {
            if (Payload.Length < 2 || The.Game == null)
            {
                return;
            }
            int length = Payload[0] | (Payload[1] << 8);
            if (Payload.Length < 2 + length)
            {
                return;
            }
            string id = Encoding.UTF8.GetString(Payload, 2, length);
            if (The.Player?.CurrentZone?.ZoneID != id || !Guest(id))
            {
                Changed(id);
                return;
            }
            byte[] packed = new byte[Payload.Length - 2 - length];
            Buffer.BlockCopy(Payload, 2 + length, packed, 0, packed.Length);
            Mirror = packed;
            MirrorZone = id;
        }

        private static void SetRole(string ZoneID, string NewOwner, int NewPlayers)
        {
            bool wasGuest = Guest(ZoneID);
            RoleZone = ZoneID;
            Owner = NewOwner;
            Players = NewPlayers;
            if (!wasGuest || !Owning(ZoneID))
            {
                return;
            }
            // The owner left and this player is next in line. The mirror becomes the real zone at the idle pass,
            // once the old owner's last update is in; until then this player stores nothing.
            Promoted = true;
        }

        // The idle pass after the hub made this guest the owner.
        private static Zone Promote(Zone Here)
        {
            Promoted = false;
            string id = Here.ZoneID;
            if (MirrorOn && Mirror != null && MirrorZone == id)
            {
                Here = QuietSwap(Here, ZoneSnapshot.Unpack(Mirror));
            }
            Mirror = null;
            Known.Clear();
            KnownZone = null;
            MirrorDue = true;
            Here.ForeachObjectWithPart("Brain", delegate(GameObject o)
            {
                if (!OnlineAvatars.Is(o))
                {
                    The.ActionManager.AddActiveObject(o);
                }
            });
            Here.Suspended = false;
            OnlineLog.Log("You now own " + id + ".");
            return Here;
        }

        // ------------------------------------------------------------------ supplying zones to the game

        // The zone to give the game in place of its own lookup, or null to let the game's lookup run.
        private static Zone Supply(string ZoneID)
        {
            if (Broken || SeenGame == null || SeenGame != The.Game || !Shared(ZoneID) || Busy.Contains(ZoneID))
            {
                return null;
            }
            ZoneManager manager = The.ZoneManager;
            bool inMemory = manager.CachedZones.TryGetValue(ZoneID, out Zone cached);
            // The common case, many times a turn: the zone is in memory and current.
            if (inMemory && (Stale.Count == 0 || !Stale.Contains(ZoneID)))
            {
                return null;
            }
            if (!OnlineLink.Connected)
            {
                return null;
            }
            if (inMemory)
            {
                if (cached == manager.ActiveZone || The.Player?.CurrentZone == cached)
                {
                    return null;
                }
                Busy.Add(ZoneID);
                try
                {
                    Stale.Remove(ZoneID);
                    if (!Fetch(ZoneID, out byte[] newer))
                    {
                        return null;
                    }
                    return Swap(cached, newer);
                }
                finally
                {
                    Busy.Remove(ZoneID);
                }
            }
            Busy.Add(ZoneID);
            try
            {
                if (Fetch(ZoneID, out byte[] data))
                {
                    // A zone this game has never built is built here first and then replaced: building one also
                    // creates things outside the zone that its contents rely on (a village's faction, for one).
                    if (!manager.IsZoneBuilt(ZoneID))
                    {
                        Zone local = manager.GetZone(ZoneID);
                        if (local != null)
                        {
                            return Swap(local, data);
                        }
                    }
                    Stopwatch watch = Stopwatch.StartNew();
                    Zone fetched = ZoneSnapshot.Read(data, ZoneID, out int errors);
                    if (fetched != null)
                    {
                        OnlineLog.Log("Got " + ZoneID + " from the hub: " + data.Length + " bytes, built in "
                            + watch.ElapsedMilliseconds + " ms, read errors " + errors + ".");
                    }
                    return fetched;
                }
                // The hub has none: the game thaws or builds it, and the hub keeps that version unless another
                // game was faster.
                Zone made = manager.GetZone(ZoneID);
                if (made == null)
                {
                    return null;
                }
                Number(made);
                byte[] packed = ZoneSnapshot.Pack(ZoneSnapshot.Write(made));
                if (!OnlineLink.StoreIfNew(ZoneID, packed, HubTimeout, out bool stored, out string problem))
                {
                    OnlineLog.Log("Could not store the new zone " + ZoneID + ": " + problem);
                    return made;
                }
                if (stored)
                {
                    OnlineLog.Log("New zone " + ZoneID + " stored on the hub: " + packed.Length + " bytes.");
                    return made;
                }
                return Fetch(ZoneID, out byte[] first) ? Swap(made, first) : made;
            }
            finally
            {
                Busy.Remove(ZoneID);
            }
        }

        // Fetches a zone of the shared world; false when the hub has none or cannot be asked. When another player
        // owns the zone, the hub asks them for a fresh copy first.
        private static bool Fetch(string ZoneID, out byte[] Data)
        {
            Data = null;
            if (!OnlineLink.Fetch(ZoneID, HubTimeout, out byte[] packed, out _))
            {
                return false;
            }
            Data = ZoneSnapshot.Unpack(packed);
            return true;
        }

        // Replaces a zone in memory with the one in the snapshot; followers of the player standing in it are
        // carried over. The zone must not be the active zone and must not hold the player. When the snapshot
        // cannot be read, the game's own lookup supplies the zone.
        private static Zone Swap(Zone Old, byte[] Data)
        {
            string id = Old.ZoneID;
            List<Held> party = TakeParty(Old);
            The.ZoneManager.SuspendZone(Old);
            ZoneSnapshot.Discard(Old);
            Stopwatch watch = Stopwatch.StartNew();
            Zone zone = ZoneSnapshot.Read(Data, id, out int errors);
            if (zone == null)
            {
                OnlineLog.Log("The hub's " + id + " could not be read; the game builds the zone again.");
                bool busy = Busy.Add(id);
                try
                {
                    zone = The.ZoneManager.GetZone(id);
                }
                finally
                {
                    if (busy)
                    {
                        Busy.Remove(id);
                    }
                }
            }
            else
            {
                OnlineLog.Log("Replaced " + id + " with the hub's: " + Data.Length + " bytes, built in "
                    + watch.ElapsedMilliseconds + " ms, read errors " + errors + ".");
            }
            if (zone != null)
            {
                Put(zone, party, false);
            }
            return zone;
        }

        // ------------------------------------------------------------------ taking objects out of a zone quietly

        // Takes the player (when in the zone) and everything the player leads out of the zone's cells without any
        // event, so the zone can be written or replaced without them.
        private static List<Held> TakeParty(Zone Z)
        {
            List<Held> held = new List<Held>();
            GameObject player = The.Player;
            if (player?.CurrentZone == Z)
            {
                held.Add(Take(player));
            }
            foreach (GameObject member in Z.GetObjects((GameObject o) => o.IsPlayerLed()))
            {
                if (member != player && member.CurrentCell != null)
                {
                    held.Add(Take(member));
                }
            }
            TakeAvatars(Z, held);
            return held;
        }

        // The other players' avatars are never written with a zone and are carried over when it is replaced.
        private static void TakeAvatars(Zone Z, List<Held> Into)
        {
            foreach (GameObject avatar in new List<GameObject>(OnlineAvatars.All()))
            {
                if (GameObject.Validate(avatar) && avatar.CurrentCell != null && avatar.CurrentZone == Z)
                {
                    Into.Add(Take(avatar));
                }
            }
        }

        private static Held Take(GameObject Object)
        {
            Cell cell = Object.CurrentCell;
            Held held = new Held { Object = Object, X = cell.X, Y = cell.Y, Index = cell.Objects.IndexOf(Object) };
            cell.Objects.Remove(Object);
            Object.Physics._CurrentCell = null;
            return held;
        }

        // Puts held objects into the cells of a zone at the places they were taken from, without any event.
        // With Same, the zone is the one they were taken from and they go back to their old place in the list.
        private static void Put(Zone Z, List<Held> Party, bool Same)
        {
            foreach (Held held in Party)
            {
                Cell cell = Z.GetCell(held.X, held.Y);
                // The cell's list checks an added object's cell, so that is set first.
                held.Object.Physics._CurrentCell = cell;
                if (Same && held.Index >= 0 && held.Index <= cell.Objects.Count)
                {
                    cell.Objects.Insert(held.Index, held.Object);
                }
                else
                {
                    cell.Objects.Add(held.Object);
                }
            }
        }

        // ------------------------------------------------------------------ storing zones on the hub

        // Gives every creature of a zone its ID. Objects only get one when asked; a guest names the creature it
        // hit by this number, so it has to be in the copy the guest receives.
        private static void Number(Zone Z)
        {
            Z.ForeachObjectWithPart("Brain", delegate(GameObject o)
            {
                if (o._BaseID <= 0)
                {
                    int assigned = o.BaseID;
                }
            });
        }

        // The zone as packed bytes without the given objects, which are back in place afterwards.
        private static byte[] PackWithout(Zone Z, List<Held> Party)
        {
            return PackWithout(Z, Party, out _);
        }

        private static byte[] PackWithout(Zone Z, List<Held> Party, out byte[] Raw)
        {
            Raw = null;
            try
            {
                Number(Z);
                Raw = ZoneSnapshot.Write(Z);
                return ZoneSnapshot.Pack(Raw);
            }
            finally
            {
                Put(Z, Party, true);
            }
        }

        // Stores the zone the player stands in, without the player. Followers are stored: they are part of what
        // the other players see there.
        private static void StoreActive(Zone Here)
        {
            Stopwatch watch = Stopwatch.StartNew();
            List<Held> party = new List<Held>();
            party.Add(Take(The.Player));
            TakeAvatars(Here, party);
            byte[] packed = PackWithout(Here, party, out byte[] raw);
            if (Players > 1)
            {
                Measure(Here.ZoneID, raw, packed.Length);
            }
            MirrorDue = false;
            SinceSent.Restart();
            if (OnlineLink.Store(Here.ZoneID, packed, HubTimeout, out string problem))
            {
                Stale.Remove(Here.ZoneID);
                MetricsManager.LogInfo("[QUDOnline] Stored " + Here.ZoneID + " (stood in): " + packed.Length
                    + " bytes, " + watch.ElapsedMilliseconds + " ms.");
            }
            else
            {
                OnlineLog.Log("Could not store " + Here.ZoneID + ": " + problem);
            }
        }

        // Measures how much smaller an update for the guests would be as the difference to the previous one, and
        // checks that the difference rebuilds the snapshot exactly. Changes nothing about what is sent.
        private static void Measure(string ZoneID, byte[] Raw, int Packed)
        {
            if (DeltaBroken || Raw == null)
            {
                return;
            }
            try
            {
                if (LastRaw != null && LastRawZone == ZoneID)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    byte[] delta = ZoneDelta.Make(LastRaw, Raw);
                    long made = watch.ElapsedMilliseconds;
                    int size = ZoneSnapshot.Pack(delta).Length;
                    watch.Restart();
                    byte[] back = ZoneDelta.Apply(LastRaw, delta);
                    long applied = watch.ElapsedMilliseconds;
                    bool good = back != null && back.Length == Raw.Length
                        && ZoneSnapshot.Hash(back) == ZoneSnapshot.Hash(Raw);
                    DeltaCount++;
                    DeltaWhole += Packed;
                    DeltaSize += size;
                    DeltaMakeTime += made;
                    DeltaApplyTime += applied;
                    if (!good)
                    {
                        DeltaFailed++;
                    }
                    MetricsManager.LogInfo("[QUDOnline] Difference for " + ZoneID + ": " + size
                        + " bytes packed against " + Packed + " whole, made in "
                        + made + " ms, applied in " + applied + " ms, check " + (good ? "passed." : "FAILED."));
                }
                LastRaw = Raw;
                LastRawZone = ZoneID;
            }
            catch (Exception x)
            {
                DeltaBroken = true;
                OnlineLog.Error("OnlineWorld.Measure", x);
            }
        }

        // Stores a zone the player has left, replacing what the hub had. Followers still on their way out are
        // left out of it. When none are left behind, the zone stops being simulated first, as vanilla does before
        // freezing one.
        private static void StoreLeft(Zone Z)
        {
            string id = Z.ZoneID;
            Stopwatch watch = Stopwatch.StartNew();
            List<Held> party = TakeParty(Z);
            if (party.Count == 0)
            {
                The.ZoneManager.SuspendZone(Z);
            }
            byte[] packed = PackWithout(Z, party);
            if (OnlineLink.Store(id, packed, HubTimeout, out string problem))
            {
                Stale.Remove(id);
                OnlineLog.Log("Stored " + id + " on the hub: " + packed.Length + " bytes, " + watch.ElapsedMilliseconds
                    + " ms.");
            }
            else
            {
                OnlineLog.Log("Could not store " + id + ": " + problem);
            }
        }

        // ------------------------------------------------------------------ the idle pass

        // A turn of the player starts (called by OnlinePresence).
        public static void BeginTurn()
        {
            TurnOpen = true;
            Forced = false;
            BodyCheck = true;
            // The creatures have just acted: the moment the guests should see.
            MirrorDue = true;
            if (Broken || The.Game == null || SeenGame != The.Game || !OnlineLink.Connected)
            {
                return;
            }
            try
            {
                Zone here = The.Player?.CurrentZone;
                if (here != null && here == The.ZoneManager.ActiveZone && here.ZoneID == LastZone)
                {
                    MirrorIfDue(here);
                }
            }
            catch (Exception x)
            {
                Broken = true;
                OnlineLog.Error("OnlineWorld.BeginTurn", x);
            }
        }

        // As the owner of a zone with guests in it: stores the zone for them when they are owed a newer copy
        // and the last one is old enough. Called at the start of each turn and from the idle pass, so the last
        // state of a quick run of turns always goes out.
        private static void MirrorIfDue(Zone Here)
        {
            if (MirrorDue && Players > 1 && !Promoted && Owning(Here.ZoneID)
                && SinceSent.ElapsedMilliseconds >= MirrorInterval)
            {
                StoreActive(Here);
            }
        }

        // Runs on the game thread many times a second while the game waits for a key (called by OnlinePresence).
        public static void Tick()
        {
            if (Broken || The.Game == null || !OnlineLink.Connected)
            {
                return;
            }
            try
            {
                Pass();
            }
            catch (Exception x)
            {
                // Never let a bug here repeat on every pass.
                Broken = true;
                OnlineLog.Error("OnlineWorld.Pass", x);
            }
        }

        private static void Pass()
        {
            ZoneManager manager = The.ZoneManager;
            Zone here = The.Player?.CurrentZone;
            if (manager == null || here == null || here != manager.ActiveZone)
            {
                return;
            }
            if (LeftWorld)
            {
                // The hub was told this player left when the last game ended.
                LeftWorld = false;
                SeenGame = null;
            }
            bool newGame = SeenGame != The.Game;
            if (newGame)
            {
                Forget();
                The.Game.RequireSystem<OnlineWorld>();
                SeenGame = The.Game;
            }
            if (HookedPlayer != The.Player)
            {
                HookedPlayer = The.Player;
                The.Player.RegisterEvent(The.Game.RequireSystem<OnlineWorld>(), TookEvent.ID);
            }
            string now = here.ZoneID;
            if (LastZone != now)
            {
                // The avatars stay behind: they are not stored with the zone that was left.
                OnlineAvatars.Clear();
                if (LastZone != null && Shared(LastZone))
                {
                    Left(LastZone);
                }
                Claim(now);
                LastZone = now;
                QuietTurn = -1;
            }
            if (newGame)
            {
                Reconcile(here);
                here = The.Player.CurrentZone;
            }
            if (!Shared(now))
            {
                return;
            }
            SendBody(now);
            OnlineAvatars.Sync();
            bool acted = TurnOpen && The.Player.Energy != null && The.Player.Energy.Value < 1000;
            bool update = MirrorOn && Mirror != null && MirrorZone == now;
            // As a guest, what this player did to the mirror goes to the owner: right after acting, so the owner
            // has it before its creatures answer the round, and before an update can wipe it.
            if (Guest(now) && KnownZone == now && (acted || update || GiveTurn != XRLCore.CurrentTurn))
            {
                GiveTurn = XRLCore.CurrentTurn;
                ReportHits(here);
                GiveNew(here);
            }
            // The player's energy is spent: they have just acted, and the creatures have not answered yet (the
            // input loop of vanilla XRLCore.PlayerTurn ends after this pass). With other players in the zone the
            // game stands still here until the hub closes the round, so everyone who acts within one tick acts
            // together.
            if (TurnOpen && The.Player.Energy != null && The.Player.Energy.Value < 1000)
            {
                TurnOpen = false;
                bool forced = Forced;
                Forced = false;
                if (!forced)
                {
                    Actions++;
                }
                if (!forced && RoundsOn && RoleZone == now && Players > 1)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    if (!OnlineLink.WaitRound(now, RoundTimeout, out string problem))
                    {
                        OnlineLog.Log("The round was not waited for: " + problem);
                    }
                    long took = watch.ElapsedMilliseconds;
                    Rounds++;
                    RoundTotal += took;
                    RoundWorst = Math.Max(RoundWorst, took);
                }
            }
            if (Promoted && RoleZone == now)
            {
                here = Promote(here);
            }
            if (Guest(now))
            {
                if (update)
                {
                    byte[] packed = Mirror;
                    Mirror = null;
                    here = QuietSwap(here, ZoneSnapshot.Unpack(packed));
                    QuietTurn = -1;
                    KnownZone = null;
                }
                if (KnownZone != now)
                {
                    Learn(here);
                }
                if (QuietTurn != XRLCore.CurrentTurn)
                {
                    QuietTurn = XRLCore.CurrentTurn;
                    Quiet(here);
                }
            }
            else
            {
                MirrorIfDue(here);
            }
        }

        // With other players in the zone: sends them a copy of the player's character when they have none for
        // this zone yet, and again when what the character wears or its level has changed. The copy is made the
        // way vanilla Temporal Fugue copies the player and is destroyed once written.
        private static void SendBody(string ZoneID)
        {
            if (RoleZone != ZoneID || Players < 2)
            {
                return;
            }
            if (BodyZone == ZoneID && !BodyCheck)
            {
                return;
            }
            BodyCheck = false;
            GameObject player = The.Player;
            StringBuilder print = new StringBuilder();
            print.Append(player.Stat("Level")).Append('|').Append(player.Render?.Tile).Append('|')
                .Append(player.Render?.ColorString);
            foreach (GameObject worn in player.GetEquippedObjects())
            {
                print.Append('|').Append(worn.Blueprint);
            }
            string now = print.ToString();
            if (BodyZone == ZoneID && BodyPrint == now)
            {
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            byte[] packed;
            GameObject copy = player.DeepCopy();
            try
            {
                copy.RemoveStringProperty("OriginalPlayerBody");
                if (copy.Physics != null)
                {
                    copy.Physics._CurrentCell = null;
                }
                packed = ZoneSnapshot.Pack(ZoneSnapshot.WriteObject(copy));
            }
            finally
            {
                copy.Obliterate();
            }
            byte[] id = Encoding.UTF8.GetBytes(The.Game.GameID);
            byte[] data = new byte[1 + id.Length + packed.Length];
            data[0] = (byte)id.Length;
            Buffer.BlockCopy(id, 0, data, 1, id.Length);
            Buffer.BlockCopy(packed, 0, data, 1 + id.Length, packed.Length);
            OnlineLink.SendBody(ZoneID, data);
            BodyZone = ZoneID;
            BodyPrint = now;
            MetricsManager.LogInfo("[QUDOnline] Sent the character to the others in " + ZoneID + ": " + packed.Length
                + " bytes, " + watch.ElapsedMilliseconds + " ms.");
        }

        // The player is in another zone than at the last pass: tell the hub, which answers who owns it.
        private static void Claim(string ZoneID)
        {
            string zone = Shared(ZoneID) ? ZoneID : "";
            if (!OnlineLink.Claim(The.Game.GameID, zone, HubTimeout, out string owner, out string problem))
            {
                OnlineLog.Log("The hub did not say who owns " + ZoneID + ": " + problem);
                return;
            }
            if (zone.Length > 0)
            {
                SetRole(zone, owner, RoleZone == zone ? Players : 1);
                if (Guest(zone))
                {
                    string name = OnlinePresence.Others.TryGetValue(owner, out OnlinePresence.Other other)
                        ? other.Name : "another player";
                    OnlineLog.Log("You are a guest here; " + name + " owns this zone.");
                }
            }
            MirrorDue = true;
            Mirror = null;
            Promoted = false;
            Known.Clear();
            KnownZone = null;
        }

        // Notes everything lying in the mirror as the owner's, the hit points of its creatures, and what the
        // player carries.
        private static void Learn(Zone Z)
        {
            Known.Clear();
            Health.Clear();
            for (int x = 0; x < Z.Width; x++)
            {
                for (int y = 0; y < Z.Height; y++)
                {
                    foreach (GameObject lying in Z.GetCell(x, y).Objects)
                    {
                        Known.Add(lying);
                        if (lying.IsPlayer() || lying.IsPlayerLed() || OnlineAvatars.Is(lying))
                        {
                            continue;
                        }
                        if (lying.Brain != null ? lying._BaseID > 0 : (!lying.Takeable && lying.HasHitpoints()))
                        {
                            Health[lying] = new Noted
                            {
                                ID = lying.Brain != null ? lying._BaseID : 0,
                                X = x,
                                Y = y,
                                Blueprint = lying.Blueprint,
                                HP = lying.hitpoints
                            };
                        }
                    }
                }
            }
            Carried = CountCarried();
            KnownZone = Z.ZoneID;
        }

        private static Dictionary<string, int> CountCarried()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (GameObject item in The.Player.GetInventoryAndEquipment())
            {
                counts.TryGetValue(item.Blueprint, out int have);
                counts[item.Blueprint] = have + item.Count;
            }
            return counts;
        }

        // Tells the owner which creatures of the mirror have lost hit points or died since the mirror was taken
        // in or last looked at.
        private static void ReportHits(Zone Z)
        {
            if (Health.Count == 0)
            {
                return;
            }
            string me = The.Game.GameID;
            List<KeyValuePair<GameObject, Noted>> changed = null;
            foreach (KeyValuePair<GameObject, Noted> entry in Health)
            {
                GameObject thing = entry.Key;
                if (!GameObject.Validate(thing) || thing.IsInGraveyard() || thing.CurrentCell == null
                    || thing.hitpoints != entry.Value.HP)
                {
                    if (changed == null)
                    {
                        changed = new List<KeyValuePair<GameObject, Noted>>();
                    }
                    changed.Add(entry);
                }
            }
            if (changed == null)
            {
                return;
            }
            foreach (KeyValuePair<GameObject, Noted> entry in changed)
            {
                GameObject thing = entry.Key;
                Noted noted = entry.Value;
                bool dead = !GameObject.Validate(thing) || thing.IsInGraveyard() || thing.CurrentCell == null
                    || thing.hitpoints <= 0;
                int damage = dead ? noted.HP : noted.HP - thing.hitpoints;
                if (dead)
                {
                    Health.Remove(thing);
                }
                else
                {
                    noted.HP = thing.hitpoints;
                }
                if (!dead && damage <= 0)
                {
                    continue;
                }
                damage = Math.Max(damage, 0);
                string text = noted.ID > 0
                    ? me + "\t" + noted.ID + "\t" + damage + "\t" + (dead ? "1" : "0")
                    : me + "\t" + noted.X + "\t" + noted.Y + "\t" + noted.Blueprint + "\t" + damage + "\t"
                        + (dead ? "1" : "0");
                byte[] words = Encoding.UTF8.GetBytes(text);
                byte[] body = new byte[1 + words.Length];
                body[0] = noted.ID > 0 ? ActHit : ActBreak;
                Buffer.BlockCopy(words, 0, body, 1, words.Length);
                OnlineLink.SendAct(Z.ZoneID, body);
                HitsSent++;
                MetricsManager.LogInfo("[QUDOnline] Told the owner of " + Z.ZoneID + ": " + noted.Blueprint
                    + (noted.ID > 0 ? " (" + noted.ID + ")" : " at " + noted.X + "," + noted.Y)
                    + (dead ? " destroyed" : " hit for " + damage) + ".");
            }
        }

        // Sends the owner every item that has appeared in the mirror's cells since it was last looked at and that
        // the player no longer carries: what this player dropped or threw. Other new items (the corpse and drops
        // of a creature killed in the mirror) are not the player's; the owner's real ones come with the next update.
        private static void GiveNew(Zone Z)
        {
            List<GameObject> fresh = null;
            for (int x = 0; x < Z.Width; x++)
            {
                for (int y = 0; y < Z.Height; y++)
                {
                    foreach (GameObject lying in Z.GetCell(x, y).Objects)
                    {
                        if (Known.Add(lying) && lying.Takeable && !lying.IsPlayer() && !lying.IsPlayerLed())
                        {
                            if (fresh == null)
                            {
                                fresh = new List<GameObject>();
                            }
                            fresh.Add(lying);
                        }
                    }
                }
            }
            Dictionary<string, int> carried = CountCarried();
            Dictionary<string, int> before = Carried;
            Carried = carried;
            if (fresh == null)
            {
                return;
            }
            foreach (GameObject item in fresh)
            {
                before.TryGetValue(item.Blueprint, out int had);
                carried.TryGetValue(item.Blueprint, out int have);
                if (had - have < item.Count)
                {
                    continue;
                }
                before[item.Blueprint] = had - item.Count;
                List<Held> held = new List<Held>();
                held.Add(Take(item));
                byte[] data;
                try
                {
                    data = ZoneSnapshot.WriteObject(item);
                }
                finally
                {
                    Put(Z, held, true);
                }
                byte[] body = new byte[3 + data.Length];
                body[0] = ActGive;
                body[1] = (byte)held[0].X;
                body[2] = (byte)held[0].Y;
                Buffer.BlockCopy(data, 0, body, 3, data.Length);
                OnlineLink.SendAct(Z.ZoneID, body);
                MetricsManager.LogInfo("[QUDOnline] Told the owner of " + Z.ZoneID + " about " + item.Blueprint
                    + " put down at " + held[0].X + "," + held[0].Y + ".");
            }
        }

        // As a guest: tells the owner what the player just took, so it leaves the real zone too.
        private static void Took(GameObject Item)
        {
            Zone here = The.Player?.CurrentZone;
            Cell cell = The.Player?.CurrentCell;
            if (Broken || here == null || cell == null || SeenGame != The.Game || !Guest(here.ZoneID)
                || !OnlineLink.Connected)
            {
                return;
            }
            // Should the player put it down again, it is theirs to give.
            Known.Remove(Item);
            string text = cell.X + "\t" + cell.Y + "\t" + Item._BaseID + "\t" + Item.Blueprint + "\t" + Item.Count;
            byte[] words = Encoding.UTF8.GetBytes(text);
            byte[] body = new byte[1 + words.Length];
            body[0] = ActTake;
            Buffer.BlockCopy(words, 0, body, 1, words.Length);
            OnlineLink.SendAct(here.ZoneID, body);
        }

        // The zone the player was in at the last pass. An owner's zone is stored; a guest's mirror is only marked
        // out of date, so the next visit fetches it again.
        private static void Left(string ZoneID)
        {
            ZoneManager manager = The.ZoneManager;
            if (!manager.CachedZones.TryGetValue(ZoneID, out Zone zone) || zone == manager.ActiveZone)
            {
                return;
            }
            if (Guest(ZoneID) || (Promoted && RoleZone == ZoneID))
            {
                Stale.Add(ZoneID);
                return;
            }
            StoreLeft(zone);
        }

        // Takes the creatures of a mirror out of the game's turn order, as vanilla ZoneManager.SuspendZone does
        // for a suspended zone. The player and their followers keep acting.
        private static void Quiet(Zone Z)
        {
            var manager = The.ActionManager;
            var queue = manager.ActionQueue;
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                GameObject actor = queue[i];
                if (actor != null && actor.CurrentZone == Z && !actor.IsPlayer() && !actor.IsPlayerLed())
                {
                    manager.RemoveActiveObject(i);
                }
            }
        }

        // Replaces the mirror the player stands in with the owner's newer copy, without the game noticing a zone
        // change: no leave or enter events, no zone message, no creatures woken. The player's party and explored
        // map are carried over. (The noisy way is ReplaceHere.)
        private static Zone QuietSwap(Zone Old, byte[] Data)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ZoneManager manager = The.ZoneManager;
            string id = Old.ZoneID;
            Zone elsewhere = manager.GetZone(Old.GetZoneWorld());
            bool[] explored = new bool[Old.Width * Old.Height];
            for (int x = 0; x < Old.Width; x++)
            {
                for (int y = 0; y < Old.Height; y++)
                {
                    explored[x + y * Old.Width] = Old.GetExplored(x, y);
                }
            }
            List<Held> party = TakeParty(Old);
            Zone zone = null;
            bool busy = Busy.Add(id);
            try
            {
                // No zone with this ID may be active or in memory while the new one is read.
                manager.ActiveZone = elsewhere;
                manager.SuspendZone(Old);
                ZoneSnapshot.Discard(Old);
                zone = ZoneSnapshot.Read(Data, id, out _);
                if (zone == null)
                {
                    OnlineLog.Log("A mirror update of " + id + " could not be read; the game builds the zone again.");
                    zone = manager.GetZone(id);
                }
            }
            finally
            {
                if (busy)
                {
                    Busy.Remove(id);
                }
                // Whatever went wrong, the party must stand somewhere again.
                if (zone == null)
                {
                    zone = manager.GetZone(id);
                }
                for (int x = 0; x < zone.Width; x++)
                {
                    for (int y = 0; y < zone.Height; y++)
                    {
                        if (explored[x + y * zone.Width])
                        {
                            zone.SetExplored(x, y, true);
                        }
                    }
                }
                manager.ActiveZone = zone;
                ZoneManager.ZoneGenerationContext = zone;
                zone.Suspended = false;
                Put(zone, party, false);
            }
            long took = watch.ElapsedMilliseconds;
            MirrorCount++;
            MirrorTotal += took;
            MirrorWorst = Math.Max(MirrorWorst, took);
            return zone;
        }

        // After connecting, or loading a game while connected: bring the zones in memory in line with the hub.
        private static void Reconcile(Zone Here)
        {
            if (!OnlineLink.List(HubTimeout, out HashSet<string> keys, out string problem))
            {
                OnlineLog.Log("The hub's zones are not known (" + problem + "); zones in memory stay as they are.");
                return;
            }
            string mine = The.Game.GetStringGameState(SeedState);
            if (OnlineLink.Seed(HubTimeout, false, out string seed, out _, out _) && seed != mine)
            {
                OnlineLog.Log(
                    "This game was not made for the hub's world (its seed differs): the two worlds will be mixed.");
            }
            ZoneManager manager = The.ZoneManager;
            int stale = 0;
            int stored = 0;
            foreach (Zone zone in new List<Zone>(manager.CachedZones.Values))
            {
                if (zone == Here || !Shared(zone.ZoneID))
                {
                    continue;
                }
                if (keys.Contains(zone.ZoneID))
                {
                    Stale.Add(zone.ZoneID);
                    stale++;
                }
                else
                {
                    StoreLeft(zone);
                    stored++;
                }
            }
            OnlineLog.Log("The hub holds " + keys.Count + " zone(s); of the zones in memory " + stale
                + " are out of date and " + stored + " were stored.");
            string id = Here.ZoneID;
            if (!Shared(id))
            {
                return;
            }
            if (!keys.Contains(id) && !Guest(id))
            {
                StoreActive(Here);
                OnlineLog.Log("Stored " + id + " on the hub.");
                return;
            }
            // The hub's copy replaces the one stood in; for a guest the hub asks the owner for it first.
            if (Fetch(id, out byte[] data))
            {
                ReplaceHere((Zone old) => Swap(old, data));
            }
        }

        // Modelled on vanilla ZoneManager.RebuildActiveZone: the player and their followers step out, another
        // zone becomes the active one, Replace is given the zone they stood in and returns the zone they step
        // back into (the same one or its replacement).
        public static void ReplaceHere(Func<Zone, Zone> Replace)
        {
            GameObject player = The.Player;
            ZoneManager manager = The.ZoneManager;
            Zone old = player.CurrentZone;
            if (old != manager.ActiveZone)
            {
                OnlineLog.Log("The player is not in the active zone.");
                return;
            }
            string id = old.ZoneID;
            OnlineAvatars.Clear();
            List<GameObject> party = old.GetObjects((GameObject o) => o.IsPlayerLed());
            party.Remove(player);
            party.Insert(0, player);
            List<int[]> places = new List<int[]>();
            foreach (GameObject member in party)
            {
                Cell cell = member.CurrentCell;
                places.Add(new int[] { cell.X, cell.Y });
                cell.RemoveObject(member);
            }
            if (old.IsWorldMap())
            {
                manager.SetActiveZone(manager.GetZone("JoppaWorld.0.0.0.0.0"));
            }
            else
            {
                manager.SetActiveZone(old.GetZoneWorld());
            }
            Zone zone = null;
            bool busy = Busy.Add(id);
            try
            {
                zone = Replace(old);
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineWorld.ReplaceHere " + id, x);
            }
            finally
            {
                if (busy)
                {
                    Busy.Remove(id);
                }
            }
            // Whatever went wrong, the party must stand somewhere again.
            if (zone == null)
            {
                zone = manager.GetZone(id);
            }
            manager.SetActiveZone(zone);
            for (int i = 0; i < party.Count; i++)
            {
                Cell cell = zone.GetCell(places[i][0], places[i][1]);
                if (i == 0)
                {
                    cell.AddObject(party[i], Forced: true, System: true);
                    manager.ProcessGoToPartyLeader();
                }
                else
                {
                    cell.AddObject(party[i]);
                }
            }
            OnlineLog.Log("Stepped back into " + id + " with " + (party.Count - 1) + " follower(s).");
        }
    }
}
