using System;
using System.IO;
using System.IO.Compression;
using XRL;
using XRL.World;

namespace QudOnline
{
    // A whole zone as bytes, written and read with the game's own serializer. Modelled on vanilla
    // ZoneManager.FreezeZone and TryThawZone, minus the save database: this is the form a zone will travel in
    // between players. The player's body is never part of a snapshot; a reference to it is read back as the
    // reading game's own player.
    public static class ZoneSnapshot
    {
        // The marker vanilla writes at the start of a frozen zone.
        private const int Magic = 123457;

        // No zone comes near this; a packed snapshot claiming more is damaged.
        private const int MaxSize = 32 * 1024 * 1024;

        public static byte[] Write(Zone Z)
        {
            SerializationWriter writer = SerializationWriter.Get();
            try
            {
                writer.Start(XRLGame.SaveVersion);
                writer.Write(Magic);
                writer.Write(typeof(XRLGame).Assembly.GetName().Version.ToString());
                Zone.Save(writer, Z);
                writer.FinalizeWrite();
                int length = (int)writer.Stream.Position;
                byte[] data = new byte[length];
                Buffer.BlockCopy(writer.Stream.GetBuffer(), 0, data, 0, length);
                return data;
            }
            finally
            {
                SerializationWriter.Release(writer);
            }
        }

        // One object, with what it carries, as bytes. It must not be in a cell or an inventory while written.
        public static byte[] WriteObject(GameObject Object)
        {
            SerializationWriter writer = SerializationWriter.Get();
            try
            {
                writer.Start(XRLGame.SaveVersion);
                writer.WriteGameObject(Object);
                writer.FinalizeWrite();
                int length = (int)writer.Stream.Position;
                byte[] data = new byte[length];
                Buffer.BlockCopy(writer.Stream.GetBuffer(), 0, data, 0, length);
                return data;
            }
            finally
            {
                SerializationWriter.Release(writer);
            }
        }

        // The object written by WriteObject, in no cell. Null when it cannot be read.
        public static GameObject ReadObject(byte[] Data, out int Errors)
        {
            Errors = 0;
            SerializationReader reader = SerializationReader.Get();
            try
            {
                reader.Stream.SetLength(Data.Length);
                Buffer.BlockCopy(Data, 0, reader.Stream.GetBuffer(), 0, Data.Length);
                reader.Start();
                GameObject result = reader.ReadGameObject();
                reader.FinalizeRead();
                Errors = reader.Errors;
                return result;
            }
            catch (Exception x)
            {
                OnlineLog.Error("ZoneSnapshot.ReadObject", x);
                return null;
            }
            finally
            {
                SerializationReader.Release(reader);
            }
        }

        // Rebuilds a zone from a snapshot and puts it in the zone cache. No zone with that ID may be in the cache
        // or active while this runs: objects find their cells, and each other, by zone ID and object ID. Returns
        // null when the snapshot cannot be read.
        public static Zone Read(byte[] Data, string ZoneID, out int Errors)
        {
            Errors = 0;
            ZoneManager manager = The.ZoneManager;
            Zone zone = null;
            SerializationReader reader = SerializationReader.Get();
            try
            {
                reader.Stream.SetLength(Data.Length);
                Buffer.BlockCopy(Data, 0, reader.Stream.GetBuffer(), 0, Data.Length);
                reader.Start();
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("not a zone snapshot");
                }
                reader.ReadString();
                zone = Zone.Load(reader, ZoneID);
                // The zone must be in the cache before the objects are finished: their cells are looked up by zone ID.
                manager.AddCachedZone(zone);
                reader.FinalizeRead();
                Errors = reader.Errors;
            }
            catch (Exception x)
            {
                OnlineLog.Error("ZoneSnapshot.Read " + ZoneID, x);
                manager.CachedZones.Remove(ZoneID);
                return null;
            }
            finally
            {
                SerializationReader.Release(reader);
            }
            ZoneManager.PaintWalls(zone);
            ZoneManager.PaintWater(zone);
            zone.Thawed(0);
            return zone;
        }

        // Drops a zone that is about to be replaced, as vanilla does after freezing one: out of the cache, its
        // objects back to the pool, and the game's last ID lookup forgotten so it cannot return one of them.
        // The zone must be suspended and must not be the active zone.
        public static void Discard(Zone Old)
        {
            The.ZoneManager.CachedZones.Remove(Old.ZoneID);
            Old.Release();
            The.Game.lastFind = null;
            The.Game.lastFindId = null;
        }

        public static int CountObjects(Zone Z)
        {
            int count = 0;
            for (int x = 0; x < Z.Width; x++)
            {
                for (int y = 0; y < Z.Height; y++)
                {
                    count += Z.GetCell(x, y).Objects.Count;
                }
            }
            return count;
        }

        // The form a snapshot travels in: its length (4 bytes, little-endian), then the snapshot gzipped.
        public static byte[] Pack(byte[] Data)
        {
            using (MemoryStream packed = new MemoryStream())
            {
                packed.WriteByte((byte)Data.Length);
                packed.WriteByte((byte)(Data.Length >> 8));
                packed.WriteByte((byte)(Data.Length >> 16));
                packed.WriteByte((byte)(Data.Length >> 24));
                using (GZipStream gzip = new GZipStream(packed, CompressionMode.Compress, true))
                {
                    gzip.Write(Data, 0, Data.Length);
                }
                return packed.ToArray();
            }
        }

        public static byte[] Unpack(byte[] Packed)
        {
            if (Packed.Length < 4)
            {
                throw new InvalidDataException("packed snapshot too short");
            }
            int length = Packed[0] | (Packed[1] << 8) | (Packed[2] << 16) | (Packed[3] << 24);
            if (length < 0 || length > MaxSize)
            {
                throw new InvalidDataException("packed snapshot claims " + length + " bytes");
            }
            byte[] data = new byte[length];
            using (MemoryStream packed = new MemoryStream(Packed, 4, Packed.Length - 4))
            using (GZipStream gzip = new GZipStream(packed, CompressionMode.Decompress))
            {
                int have = 0;
                while (have < length)
                {
                    int got = gzip.Read(data, have, length - have);
                    if (got <= 0)
                    {
                        throw new InvalidDataException("packed snapshot ends after " + have + " of " + length
                            + " bytes");
                    }
                    have += got;
                }
            }
            return data;
        }

        // FNV-1a, to tell whether two snapshots are the same bytes.
        public static uint Hash(byte[] Data)
        {
            uint hash = 2166136261;
            for (int i = 0; i < Data.Length; i++)
            {
                hash = (hash ^ Data[i]) * 16777619;
            }
            return hash;
        }
    }
}
