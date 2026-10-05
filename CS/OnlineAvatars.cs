using System;
using System.Collections.Generic;
using System.Text;
using XRL;
using XRL.World;
using XRL.World.Parts;

namespace QudOnline
{
    // The other players as real objects. Each player's game sends a copy of its character (made with
    // GameObject.DeepCopy, as vanilla Temporal Fugue copies the player); every other game in the zone builds an
    // object from it, the avatar, and keeps it on the cell that player's presence reports. Avatars are put into
    // and taken out of cells directly, without events: another player's step must not set off traps here.
    // They are never part of a stored zone or of a save (OnlineWorld takes them out for every write).
    public static class OnlineAvatars
    {
        // The latest body each player sent, packed.
        private static readonly Dictionary<string, byte[]> Bodies = new Dictionary<string, byte[]>();

        private static readonly Dictionary<string, GameObject> Avatars = new Dictionary<string, GameObject>();

        private static bool Broken;

        public static int HurtsForwarded;

        public static bool Has(string PlayerID)
        {
            return Avatars.ContainsKey(PlayerID);
        }

        // The avatar of a player in the zone the player stands in, or null.
        public static GameObject Get(string PlayerID)
        {
            return Avatars.TryGetValue(PlayerID, out GameObject avatar) && GameObject.Validate(avatar) ? avatar : null;
        }

        // Something in this game hit a player's avatar. The copy takes nothing; the hit goes to that player's
        // game. Avatars stand in every game and a guest's mirror holds copies of the owner's hazards, so a hit is
        // only passed on by the game that owns the zone, or when this game's own player (or a follower) dealt it.
        public static void Damaged(string PlayerID, Damage Hit, GameObject Actor)
        {
            try
            {
                if (Hit == null || Hit.Amount <= 0 || PlayerID == null || The.Game == null || !OnlineLink.Connected)
                {
                    return;
                }
                bool mine = Actor != null && (Actor.IsPlayer() || Actor.IsPlayerLed());
                if (!mine && !OnlineWorld.OwnsHere())
                {
                    return;
                }
                string kind = "none";
                string name = "something";
                int id = 0;
                if (Actor != null)
                {
                    kind = mine ? "player" : "creature";
                    name = Actor.IsPlayer() ? The.Game.PlayerName : Actor.DisplayNameStripped;
                    id = Actor._BaseID;
                }
                string text = The.Game.GameID + "\t" + kind + "\t" + id + "\t"
                    + (name ?? "something").Replace('\t', ' ')
                    + "\t" + Hit.Amount + "\t" + string.Join(",", Hit.Attributes);
                OnlineLink.SendTell(PlayerID, Encoding.UTF8.GetBytes(text));
                HurtsForwarded++;
                MetricsManager.LogInfo("[QUDOnline] Passed on a hit of " + Hit.Amount + " by " + name
                    + " to another player.");
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineAvatars.Damaged", x);
            }
        }

        public static bool Is(GameObject Object)
        {
            return Avatars.ContainsValue(Object);
        }

        public static IEnumerable<GameObject> All()
        {
            return Avatars.Values;
        }

        // Another game's data is gone or another game was loaded: the objects belong to a world that is no longer
        // there, so they are only forgotten.
        public static void Forget()
        {
            Avatars.Clear();
            Bodies.Clear();
            Broken = false;
        }

        // Removes every avatar from the zone; they are built again from the bodies at the next Sync.
        public static void Clear()
        {
            foreach (string id in new List<string>(Avatars.Keys))
            {
                Remove(id);
            }
        }

        // A player's body arrived. Payload: key length (2 bytes), zone, ID length (1 byte), game ID, packed object.
        public static void Received(byte[] Payload)
        {
            try
            {
                if (Payload.Length < 3)
                {
                    return;
                }
                int at = 2 + (Payload[0] | (Payload[1] << 8));
                if (Payload.Length <= at)
                {
                    return;
                }
                int idLength = Payload[at];
                if (Payload.Length <= at + 1 + idLength)
                {
                    return;
                }
                string id = Encoding.UTF8.GetString(Payload, at + 1, idLength);
                byte[] packed = new byte[Payload.Length - at - 1 - idLength];
                Buffer.BlockCopy(Payload, at + 1 + idLength, packed, 0, packed.Length);
                Bodies[id] = packed;
                // Built from the old body: make it again.
                Remove(id);
                Sync();
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineAvatars.Received", x);
            }
        }

        // Makes the avatars in the zone the player stands in match the other players' presence: one for each
        // player here whose body is known, on the cell they report; none for anyone else.
        public static void Sync()
        {
            if (Broken || The.Game == null)
            {
                return;
            }
            try
            {
                Zone here = The.Player?.CurrentZone;
                if (here == null || here != The.ZoneManager.ActiveZone)
                {
                    return;
                }
                string now = here.ZoneID;
                foreach (string id in new List<string>(Avatars.Keys))
                {
                    GameObject avatar = Avatars[id];
                    if (!GameObject.Validate(avatar) || avatar.CurrentCell == null || avatar.CurrentZone != here)
                    {
                        // Lost with a zone that was replaced or left.
                        Avatars.Remove(id);
                    }
                    else if (!OnlinePresence.Others.TryGetValue(id, out OnlinePresence.Other other)
                        || other.ZoneID != now)
                    {
                        Remove(id);
                    }
                }
                foreach (OnlinePresence.Other other in OnlinePresence.Others.Values)
                {
                    if (other.ZoneID != now || !Bodies.TryGetValue(other.ID, out byte[] packed))
                    {
                        continue;
                    }
                    Cell target = here.GetCell(other.X, other.Y);
                    if (target == null)
                    {
                        continue;
                    }
                    if (!Avatars.TryGetValue(other.ID, out GameObject avatar))
                    {
                        avatar = Build(other.ID, packed);
                        if (avatar == null)
                        {
                            Bodies.Remove(other.ID);
                            continue;
                        }
                        Avatars[other.ID] = avatar;
                    }
                    if (avatar.CurrentCell != target)
                    {
                        Place(avatar, target);
                    }
                    // The copy shows the real character's health.
                    if (other.HP > 0 && avatar.hitpoints != other.HP)
                    {
                        avatar.hitpoints = other.HP;
                    }
                }
            }
            catch (Exception x)
            {
                // Never let a bug here repeat on every pass.
                Broken = true;
                OnlineLog.Error("OnlineAvatars.Sync", x);
            }
        }

        private static GameObject Build(string PlayerID, byte[] Packed)
        {
            GameObject avatar = ZoneSnapshot.ReadObject(ZoneSnapshot.Unpack(Packed), out int errors);
            if (avatar == null || avatar.Physics == null)
            {
                OnlineLog.Log("The body of another player could not be read.");
                return null;
            }
            avatar.Physics._CurrentCell = null;
            avatar.Brain?.Goals.Clear();
            if (avatar.HasStat("XPValue"))
            {
                avatar.GetStat("XPValue").BaseValue = 0;
            }
            avatar.AddPart(new OnlineAvatar { PlayerID = PlayerID });
            MetricsManager.LogInfo("[QUDOnline] Built the avatar of " + avatar.DisplayName + " from " + Packed.Length
                + " bytes, read errors " + errors + ".");
            return avatar;
        }

        // Moves an avatar to a cell without events.
        private static void Place(GameObject Avatar, Cell Target)
        {
            Cell old = Avatar.CurrentCell;
            if (old != null)
            {
                old.Objects.Remove(Avatar);
            }
            // The cell's list checks an added object's cell, so that is set first.
            Avatar.Physics._CurrentCell = Target;
            Target.Objects.Add(Avatar);
        }

        private static void Remove(string PlayerID)
        {
            if (!Avatars.TryGetValue(PlayerID, out GameObject avatar))
            {
                return;
            }
            Avatars.Remove(PlayerID);
            try
            {
                if (GameObject.Validate(avatar))
                {
                    Cell cell = avatar.CurrentCell;
                    if (cell != null)
                    {
                        cell.Objects.Remove(avatar);
                        avatar.Physics._CurrentCell = null;
                    }
                    avatar.Obliterate();
                }
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineAvatars.Remove", x);
            }
        }
    }
}
