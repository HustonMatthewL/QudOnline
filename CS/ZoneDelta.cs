using System;
using System.Collections.Generic;
using System.IO;

namespace QudOnline
{
    // The difference between two snapshots of a zone, so that only what changed has to travel. The older
    // snapshot is indexed in blocks by a rolling hash; every stretch of the newer one that also occurs in the
    // older one becomes "copy so many bytes from there", the rest is carried as it is (the method rsync uses).
    // Format: length and hash of the older snapshot, length and hash of the newer one (4 bytes each,
    // little-endian), then instructions: 0, length, bytes; or 1, offset, length. Numbers in instructions are
    // written seven bits at a time, low bits first. online_hub.py has the same thing in Python, with a test.
    public static class ZoneDelta
    {
        private const int Block = 32;
        private const uint Multiplier = 1000003;
        private const int HeaderSize = 16;

        public static byte[] Make(byte[] Old, byte[] New)
        {
            using (MemoryStream output = new MemoryStream())
            {
                PutInt(output, Old.Length);
                PutInt(output, (int)ZoneSnapshot.Hash(Old));
                PutInt(output, New.Length);
                PutInt(output, (int)ZoneSnapshot.Hash(New));
                Dictionary<uint, int> index = new Dictionary<uint, int>();
                for (int i = 0; i + Block <= Old.Length; i += Block)
                {
                    uint hash = HashAt(Old, i);
                    if (!index.ContainsKey(hash))
                    {
                        index[hash] = i;
                    }
                }
                // The weight of the byte that leaves the window when it moves on by one.
                uint top = 1;
                for (int k = 0; k < Block - 1; k++)
                {
                    top = unchecked(top * Multiplier);
                }
                int length = New.Length;
                int at = 0;
                // Where the bytes not yet written out begin.
                int pending = 0;
                bool windowed = length >= Block;
                uint window = windowed ? HashAt(New, 0) : 0;
                while (at < length)
                {
                    int from;
                    if (windowed && index.TryGetValue(window, out from) && Same(Old, from, New, at, Block))
                    {
                        int run = Block;
                        while (from + run < Old.Length && at + run < length && Old[from + run] == New[at + run])
                        {
                            run++;
                        }
                        // The match may reach back into bytes that were about to be carried as they are.
                        while (at > pending && from > 0 && Old[from - 1] == New[at - 1])
                        {
                            from--;
                            at--;
                            run++;
                        }
                        if (at > pending)
                        {
                            output.WriteByte(0);
                            PutNumber(output, at - pending);
                            output.Write(New, pending, at - pending);
                        }
                        output.WriteByte(1);
                        PutNumber(output, from);
                        PutNumber(output, run);
                        at += run;
                        pending = at;
                        windowed = at + Block <= length;
                        if (windowed)
                        {
                            window = HashAt(New, at);
                        }
                    }
                    else
                    {
                        if (windowed && at + Block < length)
                        {
                            window = unchecked((window - (uint)New[at] * top) * Multiplier + (uint)New[at + Block]);
                        }
                        else
                        {
                            windowed = false;
                        }
                        at++;
                    }
                }
                if (length > pending)
                {
                    output.WriteByte(0);
                    PutNumber(output, length - pending);
                    output.Write(New, pending, length - pending);
                }
                return output.ToArray();
            }
        }

        // The newer snapshot, rebuilt from the older one and the difference. Null when the difference was made
        // from another snapshot than the one given, is damaged, or does not give the snapshot it promises.
        public static byte[] Apply(byte[] Old, byte[] Delta)
        {
            if (Delta.Length < HeaderSize || GetInt(Delta, 0) != Old.Length
                || GetInt(Delta, 4) != (int)ZoneSnapshot.Hash(Old))
            {
                return null;
            }
            int length = GetInt(Delta, 8);
            if (length < 0)
            {
                return null;
            }
            byte[] result = new byte[length];
            int written = 0;
            int at = HeaderSize;
            while (at < Delta.Length)
            {
                byte kind = Delta[at++];
                if (kind == 0)
                {
                    int count = GetNumber(Delta, ref at);
                    if (count < 0 || at + count > Delta.Length || written + count > length)
                    {
                        return null;
                    }
                    Buffer.BlockCopy(Delta, at, result, written, count);
                    at += count;
                    written += count;
                }
                else if (kind == 1)
                {
                    int from = GetNumber(Delta, ref at);
                    int count = GetNumber(Delta, ref at);
                    if (from < 0 || count < 0 || from + count > Old.Length || written + count > length)
                    {
                        return null;
                    }
                    Buffer.BlockCopy(Old, from, result, written, count);
                    written += count;
                }
                else
                {
                    return null;
                }
            }
            if (written != length || GetInt(Delta, 12) != (int)ZoneSnapshot.Hash(result))
            {
                return null;
            }
            return result;
        }

        private static uint HashAt(byte[] Data, int At)
        {
            uint hash = 0;
            for (int k = 0; k < Block; k++)
            {
                hash = unchecked(hash * Multiplier + (uint)Data[At + k]);
            }
            return hash;
        }

        private static bool Same(byte[] A, int AtA, byte[] B, int AtB, int Count)
        {
            if (AtA + Count > A.Length || AtB + Count > B.Length)
            {
                return false;
            }
            for (int k = 0; k < Count; k++)
            {
                if (A[AtA + k] != B[AtB + k])
                {
                    return false;
                }
            }
            return true;
        }

        private static void PutInt(Stream Into, int Value)
        {
            Into.WriteByte((byte)Value);
            Into.WriteByte((byte)(Value >> 8));
            Into.WriteByte((byte)(Value >> 16));
            Into.WriteByte((byte)(Value >> 24));
        }

        private static int GetInt(byte[] From, int At)
        {
            return From[At] | (From[At + 1] << 8) | (From[At + 2] << 16) | (From[At + 3] << 24);
        }

        private static void PutNumber(Stream Into, int Value)
        {
            uint left = (uint)Value;
            while (left >= 128)
            {
                Into.WriteByte((byte)((left & 127) | 128));
                left >>= 7;
            }
            Into.WriteByte((byte)left);
        }

        // -1 when the number runs past the end.
        private static int GetNumber(byte[] From, ref int At)
        {
            uint value = 0;
            int shift = 0;
            while (At < From.Length && shift < 35)
            {
                byte next = From[At++];
                value |= (uint)(next & 127) << shift;
                if (next < 128)
                {
                    return (int)value;
                }
                shift += 7;
            }
            return -1;
        }
    }
}
