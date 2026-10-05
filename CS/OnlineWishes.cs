using System;
using System.Collections.Generic;
using System.Diagnostics;
using XRL;
using XRL.Wish;
using XRL.World;

namespace QudOnline
{
    // Test wishes (use a spare save):
    //   onlineSnapshot          measure a snapshot of the zone you stand in; changes nothing.
    //   onlineReload:<dir>      replace the neighbouring zone (N, S, E, W, U, D) with its own snapshot.
    //   onlineReloadHere        replace the zone you stand in with its own snapshot.
    //   onlineConnect           connect to the hub on this machine; onlineConnect:<host>:<port> for another.
    //   onlineDisconnect
    //   onlineWho               list the other players and where they are.
    //   onlineSeed              this game's world seed and the hub's.
    //   onlineZones             how many zones the hub holds, and which zones in memory are out of date.
    //   onlineRole              whether you own the zone you stand in or are a guest, and how mirror updates went.
    //   onlineMirror:on|off     as a guest, apply the owner's updates or not.
    //   onlineRounds:on|off     after acting, wait for the round to close when others are in the zone, or not.
    //   onlineBackground:on|off keep this game running while its window is not focused (on while connected).
    //   onlineBackground        say whether that is on and what Unity was last told.
    //   onlinePing              time five round trips to the hub.
    //   onlineSendZone:<dir>    as onlineReload, but the snapshot goes to the hub and is fetched back.
    //   onlineSendZone          the same for the zone you stand in.
    [HasWishCommand]
    public static class OnlineWishes
    {
        private const string DefaultHost = OnlineLink.DefaultHost;
        private const int DefaultPort = OnlineLink.DefaultPort;

        // How long the game waits for the hub before going on without it.
        private const int HubTimeout = 5000;

        [WishCommand(Command = "onlineConnect")]
        public static void Connect()
        {
            Connect(DefaultHost + ":" + DefaultPort);
        }

        [WishCommand(Command = "onlineConnect")]
        public static void Connect(string Address)
        {
            try
            {
                string address = (Address ?? "").Trim();
                string host = address;
                int port = DefaultPort;
                int colon = address.LastIndexOf(':');
                if (colon >= 0)
                {
                    host = address.Substring(0, colon);
                    if (!int.TryParse(address.Substring(colon + 1), out port))
                    {
                        OnlineLog.Log("Use onlineConnect:<host>:<port>.");
                        return;
                    }
                }
                if (host.Length == 0)
                {
                    host = DefaultHost;
                }
                if (OnlineLink.Connect(host, port, out string problem))
                {
                    OnlineLog.Log("Connected to the hub at " + host + ":" + port + ".");
                    OnlinePresence.Start();
                    OnlineWorld.Start();
                }
                else
                {
                    OnlineLog.Log("Could not connect to " + host + ":" + port + ": " + problem);
                }
            }
            catch (Exception x)
            {
                OnlineLog.Error("onlineConnect", x);
            }
        }

        [WishCommand(Command = "onlineDisconnect")]
        public static void Disconnect()
        {
            bool was = OnlineLink.Connected;
            OnlineLink.Close();
            OnlinePresence.Forget();
            OnlineLog.Log(was ? "Disconnected from the hub." : "There was no connection.");
        }

        [WishCommand(Command = "onlineWho")]
        public static void Who()
        {
            if (!OnlineLink.Connected)
            {
                OnlineLog.Log("Not connected to a hub.");
                return;
            }
            if (OnlinePresence.Others.Count == 0)
            {
                OnlineLog.Log("Nobody else is online.");
                return;
            }
            foreach (OnlinePresence.Other other in OnlinePresence.Others.Values)
            {
                OnlineLog.Log(other.Name + " is in " + other.ZoneID + " at " + other.X + "," + other.Y
                    + (OnlineAvatars.Has(other.ID) ? ", shown as an avatar." : "."));
            }
        }

        [WishCommand(Command = "onlineSeed")]
        public static void Seed()
        {
            try
            {
                string mine = The.Game.GetStringGameState(OnlineWorld.SeedState);
                OnlineLog.Log(mine.Length > 0 ? "This game was made for the world with seed " + mine + "."
                    : "This game was not made for a hub's world.");
                if (!OnlineLink.Seed(HubTimeout, false, out string seed, out _, out string problem))
                {
                    OnlineLog.Log("The hub's seed is not known: " + problem);
                    return;
                }
                OnlineLog.Log("The hub's world has seed " + seed + (seed == mine ? ": the same." : ": DIFFERENT."));
            }
            catch (Exception x)
            {
                OnlineLog.Error("onlineSeed", x);
            }
        }

        [WishCommand(Command = "onlineZones")]
        public static void Zones()
        {
            try
            {
                if (!OnlineLink.List(HubTimeout, out HashSet<string> keys, out string problem))
                {
                    OnlineLog.Log("The hub's zones are not known: " + problem);
                    return;
                }
                OnlineLog.Log("The hub holds " + keys.Count + " zone(s). " + OnlineWorld.Describe());
            }
            catch (Exception x)
            {
                OnlineLog.Error("onlineZones", x);
            }
        }

        [WishCommand(Command = "onlineRole")]
        public static void Role()
        {
            try
            {
                OnlineLog.Log(OnlineLink.Connected ? OnlineWorld.DescribeRole() : "Not connected to a hub.");
            }
            catch (Exception x)
            {
                OnlineLog.Error("onlineRole", x);
            }
        }

        [WishCommand(Command = "onlineMirror")]
        public static void Mirror(string State)
        {
            OnlineWorld.MirrorOn = (State ?? "").Trim().ToLower() != "off";
            OnlineLog.Log("Mirror updates are " + (OnlineWorld.MirrorOn ? "on." : "off."));
        }

        [WishCommand(Command = "onlineRounds")]
        public static void Rounds(string State)
        {
            OnlineWorld.RoundsOn = (State ?? "").Trim().ToLower() != "off";
            OnlineLog.Log("Rounds are " + (OnlineWorld.RoundsOn ? "on." : "off."));
        }

        [WishCommand(Command = "onlineBackground")]
        public static void Background()
        {
            OnlineLog.Log(OnlineBackground.Describe());
        }

        [WishCommand(Command = "onlineBackground")]
        public static void Background(string State)
        {
            OnlineBackground.On = (State ?? "").Trim().ToLower() != "off";
            OnlineBackground.Start();
            OnlineLog.Log(OnlineBackground.On
                ? "This game keeps running while its window is not focused, as long as it is connected."
                : "This game pauses when its window is not focused, as usual.");
        }

        [WishCommand(Command = "onlinePing")]
        public static void Ping()
        {
            try
            {
                string times = "";
                for (int i = 0; i < 5; i++)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    if (!OnlineLink.Request(OnlineLink.Ping, null, HubTimeout, out _, out string problem))
                    {
                        OnlineLog.Log("Ping failed: " + problem);
                        return;
                    }
                    times += (i > 0 ? ", " : "") + watch.Elapsed.TotalMilliseconds.ToString("0.0");
                }
                OnlineLog.Log("Ping round trips in ms: " + times + ".");
            }
            catch (Exception x)
            {
                OnlineLog.Error("onlinePing", x);
            }
        }

        [WishCommand(Command = "onlineSendZone")]
        public static void SendNeighbour(string Direction)
        {
            ReplaceNeighbour(Direction, true, "onlineSendZone");
        }

        [WishCommand(Command = "onlineSendZone")]
        public static void SendHere()
        {
            ReplaceHere(true, "onlineSendZone");
        }

        [WishCommand(Command = "onlineSnapshot")]
        public static void Snapshot()
        {
            try
            {
                Zone zone = The.Player.CurrentZone;
                Measure(zone, out _);
            }
            catch (Exception x)
            {
                OnlineLog.Error("onlineSnapshot", x);
            }
        }

        [WishCommand(Command = "onlineReload")]
        public static void ReloadNeighbour(string Direction)
        {
            ReplaceNeighbour(Direction, false, "onlineReload");
        }

        [WishCommand(Command = "onlineReloadHere")]
        public static void ReloadHere()
        {
            ReplaceHere(false, "onlineReloadHere");
        }

        private static void ReplaceNeighbour(string Direction, bool ViaHub, string Wish)
        {
            try
            {
                string direction = (Direction ?? "").Trim().ToUpper();
                Zone zone = The.Player.CurrentZone.GetZoneFromDirection(direction);
                if (zone == null || zone == The.Player.CurrentZone)
                {
                    OnlineLog.Log("No zone in direction '" + direction + "'. Use N, S, E, W, U or D.");
                    return;
                }
                bool suspended = zone.Suspended;
                zone = Rebuild(zone, ViaHub);
                if (zone != null)
                {
                    zone.Suspended = suspended;
                }
            }
            catch (Exception x)
            {
                OnlineLog.Error(Wish, x);
            }
        }

        private static void ReplaceHere(bool ViaHub, string Wish)
        {
            try
            {
                OnlineWorld.ReplaceHere((Zone old) => Rebuild(old, ViaHub));
            }
            catch (Exception x)
            {
                OnlineLog.Error(Wish, x);
            }
        }

        // Suspend the zone, snapshot it, drop it, rebuild it from the snapshot, snapshot again and compare. The
        // zone must not be the active zone and must not hold the player. If the snapshot cannot be read, the game
        // builds the zone its own way and that zone is returned.
        // With ViaHub, the snapshot is stored on the hub and the zone is rebuilt from what the hub returns.
        private static Zone Rebuild(Zone Old, bool ViaHub)
        {
            string id = Old.ZoneID;
            The.ZoneManager.SuspendZone(Old);
            byte[] before = Measure(Old, out int objectsBefore);
            if (ViaHub)
            {
                before = ThroughHub(id, before);
            }
            ZoneSnapshot.Discard(Old);
            Stopwatch watch = Stopwatch.StartNew();
            Zone zone = ZoneSnapshot.Read(before, id, out int errors);
            watch.Stop();
            if (zone == null)
            {
                OnlineLog.Log("Rebuilding " + id + " FAILED; the game builds the zone again.");
                return The.ZoneManager.GetZone(id);
            }
            int objectsAfter = ZoneSnapshot.CountObjects(zone);
            byte[] after = ZoneSnapshot.Write(zone);
            if (ViaHub && OnlineLink.Connected
                && !OnlineLink.Store(id + "|after", ZoneSnapshot.Pack(after), HubTimeout, out string problem))
            {
                OnlineLog.Log("Storing the second snapshot failed: " + problem);
            }
            OnlineLog.Log("Rebuilt " + id + " in " + watch.ElapsedMilliseconds + " ms, read errors " + errors
                + ", objects " + objectsBefore + " -> " + objectsAfter
                + ", second snapshot " + after.Length + " bytes, "
                + (ZoneSnapshot.Hash(before) == ZoneSnapshot.Hash(after) ? "identical" : "different") + " bytes.");
            return zone;
        }

        // Stores the snapshot on the hub, fetches it back and returns the fetched bytes; the local ones when the
        // hub cannot be used.
        private static byte[] ThroughHub(string ZoneID, byte[] Local)
        {
            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                byte[] packed = ZoneSnapshot.Pack(Local);
                string problem;
                if (!OnlineLink.Store(ZoneID + "|before", packed, HubTimeout, out problem)
                    || !OnlineLink.Fetch(ZoneID + "|before", HubTimeout, out packed, out problem))
                {
                    OnlineLog.Log("The hub was not used (" + problem + "); rebuilding from the local snapshot.");
                    return Local;
                }
                byte[] fetched = ZoneSnapshot.Unpack(packed);
                bool same = fetched.Length == Local.Length && ZoneSnapshot.Hash(fetched) == ZoneSnapshot.Hash(Local);
                OnlineLog.Log("Hub round trip of " + ZoneID + ": " + packed.Length
                    + " bytes each way in " + watch.ElapsedMilliseconds
                    + " ms, came back " + (same ? "unchanged." : "CHANGED; rebuilding from the local snapshot."));
                return same ? fetched : Local;
            }
            catch (Exception x)
            {
                OnlineLog.Error("hub round trip of " + ZoneID, x);
                return Local;
            }
        }

        private static byte[] Measure(Zone Z, out int Objects)
        {
            Objects = ZoneSnapshot.CountObjects(Z);
            Stopwatch watch = Stopwatch.StartNew();
            byte[] data = ZoneSnapshot.Write(Z);
            long writeTime = watch.ElapsedMilliseconds;
            watch.Restart();
            int packed = ZoneSnapshot.Pack(data).Length;
            OnlineLog.Log("Snapshot of " + Z.ZoneID + ": " + Objects + " objects in cells, " + data.Length + " bytes ("
                + packed + " compressed), written in " + writeTime + " ms, compressed in " + watch.ElapsedMilliseconds
                + " ms.");
            return data;
        }
    }
}
