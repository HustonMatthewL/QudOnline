using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace QudOnline
{
    // The connection to the hub: one TCP socket, a background thread that reads from it, and requests that wait
    // for their reply. A frame is: payload length (4 bytes, little-endian), message type (1 byte), request number
    // (4 bytes), payload. The hub answers a request with a Reply or Failure frame carrying the same request
    // number. Frames nobody is waiting for are handed to the game thread. A socket error closes the link and is
    // reported in the log; it never throws into the game's turn.
    public static class OnlineLink
    {
        public const byte Ping = 1;
        public const byte StoreZone = 2;
        public const byte FetchZone = 3;
        public const byte Reply = 4;
        public const byte Failure = 5;
        public const byte Here = 6;
        public const byte Gone = 7;
        public const byte GetSeed = 8;
        public const byte StoreNew = 9;
        public const byte ListZones = 10;
        public const byte Changed = 11;
        public const byte ClaimZone = 12;
        public const byte Role = 13;
        public const byte Want = 14;
        public const byte Mirror = 15;
        public const byte Act = 16;
        public const byte Round = 17;
        public const byte Wait = 18;
        public const byte Body = 19;
        public const byte Tell = 20;

        public const string DefaultHost = "127.0.0.1";
        public const int DefaultPort = 7777;

        // Called on the game thread: a frame that answers no request, and the connection being lost.
        public static Action<byte, byte[]> Received;
        public static Action Lost;

        private const int HeaderSize = 9;
        private const int MaxPayload = 64 * 1024 * 1024;
        private const int ConnectTimeout = 3000;
        private const int SendTimeout = 5000;

        private struct Answer
        {
            public byte Type;
            public byte[] Payload;
        }

        private static readonly object SendLock = new object();

        // Guards everything below it.
        private static readonly object AnswerLock = new object();
        private static TcpClient Client;
        private static NetworkStream Stream;
        private static int LastRequest;
        private static readonly HashSet<int> Waiting = new HashSet<int>();
        private static readonly Dictionary<int, Answer> Answers = new Dictionary<int, Answer>();

        public static bool Connected
        {
            get
            {
                lock (AnswerLock)
                {
                    return Client != null;
                }
            }
        }

        public static bool Connect(string Host, int Port, out string Problem)
        {
            Close();
            Problem = null;
            TcpClient client = new TcpClient();
            NetworkStream stream;
            try
            {
                IAsyncResult attempt = client.BeginConnect(Host, Port, null, null);
                if (!attempt.AsyncWaitHandle.WaitOne(ConnectTimeout))
                {
                    client.Close();
                    Problem = "no answer within " + ConnectTimeout + " ms";
                    return false;
                }
                client.EndConnect(attempt);
                client.NoDelay = true;
                stream = client.GetStream();
                stream.WriteTimeout = SendTimeout;
            }
            catch (Exception x)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
                Problem = x.Message;
                return false;
            }
            lock (AnswerLock)
            {
                Client = client;
                Stream = stream;
            }
            Thread reader = new Thread(() => ReadLoop(client, stream));
            reader.IsBackground = true;
            reader.Name = "QUDOnline reader";
            reader.Start();
            return true;
        }

        public static void Close()
        {
            TcpClient client;
            lock (AnswerLock)
            {
                client = Client;
                Client = null;
                Stream = null;
                Monitor.PulseAll(AnswerLock);
            }
            if (client != null)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        // Sends a request and waits for its answer. Called on the game thread, which stands still meanwhile.
        public static bool Request(byte Type, byte[] Payload, int TimeoutMs, out byte[] Result, out string Problem)
        {
            Result = null;
            Problem = null;
            int request;
            lock (AnswerLock)
            {
                if (Client == null)
                {
                    Problem = "not connected";
                    return false;
                }
                request = ++LastRequest;
                Waiting.Add(request);
            }
            try
            {
                if (!Send(Type, request, Payload ?? new byte[0], out Problem))
                {
                    return false;
                }
                Stopwatch watch = Stopwatch.StartNew();
                lock (AnswerLock)
                {
                    Answer answer;
                    while (!Answers.TryGetValue(request, out answer))
                    {
                        if (Client == null)
                        {
                            Problem = "the connection closed";
                            return false;
                        }
                        int left = TimeoutMs - (int)watch.ElapsedMilliseconds;
                        if (left <= 0)
                        {
                            Problem = "no answer within " + TimeoutMs + " ms";
                            return false;
                        }
                        Monitor.Wait(AnswerLock, left);
                    }
                    if (answer.Type == Failure)
                    {
                        Problem = "the hub says: " + Encoding.UTF8.GetString(answer.Payload);
                        return false;
                    }
                    Result = answer.Payload;
                    return true;
                }
            }
            finally
            {
                lock (AnswerLock)
                {
                    Waiting.Remove(request);
                    Answers.Remove(request);
                }
            }
        }

        // Sends a frame that expects no answer.
        public static bool Send(byte Type, byte[] Payload)
        {
            return Send(Type, 0, Payload ?? new byte[0], out _);
        }

        // Stores bytes on the hub under a key, replacing what was there.
        public static bool Store(string Key, byte[] Data, int TimeoutMs, out string Problem)
        {
            return Request(StoreZone, Keyed(Key, Data), TimeoutMs, out _, out Problem);
        }

        // Stores a zone of the shared world only if the hub has none under that key. Stored says which happened.
        public static bool StoreIfNew(string Key, byte[] Data, int TimeoutMs, out bool Stored, out string Problem)
        {
            Stored = false;
            if (!Request(StoreNew, Keyed(Key, Data), TimeoutMs, out byte[] result, out Problem))
            {
                return false;
            }
            Stored = Encoding.UTF8.GetString(result) == "stored";
            return true;
        }

        // The keys of the zones the shared world holds.
        public static bool List(int TimeoutMs, out HashSet<string> Keys, out string Problem)
        {
            Keys = new HashSet<string>();
            if (!Request(ListZones, null, TimeoutMs, out byte[] result, out Problem))
            {
                return false;
            }
            foreach (string key in Encoding.UTF8.GetString(result).Split('\n'))
            {
                if (key.Length > 0)
                {
                    Keys.Add(key);
                }
            }
            return true;
        }

        // The seed of the shared world. A game being created asks with NewGame and also gets a block number of
        // its own (1 and up) for numbering its objects; otherwise Block is 0.
        public static bool Seed(int TimeoutMs, bool NewGame, out string WorldSeed, out int Block, out string Problem)
        {
            WorldSeed = null;
            Block = 0;
            if (!Request(GetSeed, NewGame ? Encoding.UTF8.GetBytes("new") : null, TimeoutMs, out byte[] result,
                out Problem))
            {
                return false;
            }
            string[] fields = Encoding.UTF8.GetString(result).Split('\t');
            WorldSeed = fields[0];
            if (fields.Length > 1)
            {
                int.TryParse(fields[1], out Block);
            }
            return true;
        }

        // Tells the hub which zone of the shared world the player is in now ("" for none); Owner is the game ID
        // of the player who owns that zone.
        public static bool Claim(string GameID, string ZoneID, int TimeoutMs, out string Owner, out string Problem)
        {
            Owner = null;
            if (!Request(ClaimZone, Encoding.UTF8.GetBytes(GameID + "\t" + ZoneID), TimeoutMs, out byte[] result,
                out Problem))
            {
                return false;
            }
            Owner = Encoding.UTF8.GetString(result);
            return true;
        }

        // The player has just acted in a zone of the shared world with other players in it. Returns when the hub
        // closes the round: after its tick window, or as soon as every player in the zone has acted.
        public static bool WaitRound(string ZoneID, int TimeoutMs, out string Problem)
        {
            return Request(Round, Encoding.UTF8.GetBytes(ZoneID), TimeoutMs, out _, out Problem);
        }

        // A copy of the player's character for the other players in the zone. Data: ID length (1 byte), game ID,
        // packed object. No answer.
        public static bool SendBody(string ZoneID, byte[] Data)
        {
            return Send(Body, Keyed(ZoneID, Data));
        }

        // Something for one other player, wherever they are. Sent as: ID length (1 byte), their game ID, body.
        // No answer.
        public static bool SendTell(string GameID, byte[] Body)
        {
            byte[] id = Encoding.UTF8.GetBytes(GameID);
            byte[] payload = new byte[1 + id.Length + Body.Length];
            payload[0] = (byte)id.Length;
            Buffer.BlockCopy(id, 0, payload, 1, id.Length);
            Buffer.BlockCopy(Body, 0, payload, 1 + id.Length, Body.Length);
            return Send(Tell, payload);
        }

        // A guest tells the owner of a zone what it did there. No answer.
        public static bool SendAct(string ZoneID, byte[] Body)
        {
            return Send(Act, Keyed(ZoneID, Body));
        }

        private static byte[] Keyed(string Key, byte[] Data)
        {
            byte[] key = Encoding.UTF8.GetBytes(Key);
            byte[] payload = new byte[2 + key.Length + Data.Length];
            payload[0] = (byte)key.Length;
            payload[1] = (byte)(key.Length >> 8);
            Buffer.BlockCopy(key, 0, payload, 2, key.Length);
            Buffer.BlockCopy(Data, 0, payload, 2 + key.Length, Data.Length);
            return payload;
        }

        public static bool Fetch(string Key, int TimeoutMs, out byte[] Data, out string Problem)
        {
            return Request(FetchZone, Encoding.UTF8.GetBytes(Key), TimeoutMs, out Data, out Problem);
        }

        private static bool Send(byte Type, int Number, byte[] Payload, out string Problem)
        {
            Problem = null;
            TcpClient client;
            NetworkStream stream;
            lock (AnswerLock)
            {
                client = Client;
                stream = Stream;
            }
            if (stream == null)
            {
                Problem = "not connected";
                return false;
            }
            byte[] header = new byte[HeaderSize];
            PutInt(header, 0, Payload.Length);
            header[4] = Type;
            PutInt(header, 5, Number);
            try
            {
                lock (SendLock)
                {
                    stream.Write(header, 0, HeaderSize);
                    stream.Write(Payload, 0, Payload.Length);
                }
                return true;
            }
            catch (Exception x)
            {
                Problem = x.Message;
                Drop(client, "sending failed: " + x.Message);
                return false;
            }
        }

        // Runs on the reader thread until the connection ends.
        private static void ReadLoop(TcpClient client, NetworkStream stream)
        {
            string reason = "the hub closed the connection";
            try
            {
                byte[] header = new byte[HeaderSize];
                while (ReadExact(stream, header, HeaderSize))
                {
                    int length = GetInt(header, 0);
                    byte type = header[4];
                    int request = GetInt(header, 5);
                    if (length < 0 || length > MaxPayload)
                    {
                        reason = "a frame of impossible size arrived";
                        break;
                    }
                    byte[] payload = new byte[length];
                    if (!ReadExact(stream, payload, length))
                    {
                        break;
                    }
                    bool awaited;
                    lock (AnswerLock)
                    {
                        awaited = Waiting.Contains(request);
                        if (awaited)
                        {
                            Answers[request] = new Answer { Type = type, Payload = payload };
                            Monitor.PulseAll(AnswerLock);
                        }
                    }
                    if (!awaited)
                    {
                        GameManager.Instance.gameQueue.queueTask(() => Unrequested(type, payload));
                    }
                }
            }
            catch (Exception x)
            {
                reason = x.Message;
            }
            Drop(client, reason);
        }

        // A frame that answers no pending request, on the game thread.
        private static void Unrequested(byte Type, byte[] Payload)
        {
            try
            {
                Received?.Invoke(Type, Payload);
            }
            catch (Exception x)
            {
                OnlineLog.Error("message of type " + Type, x);
            }
        }

        private static void ReportLost(string Reason)
        {
            OnlineLog.Log("Disconnected from the hub: " + Reason);
            try
            {
                Lost?.Invoke();
            }
            catch (Exception x)
            {
                OnlineLog.Error("OnlineLink.Lost", x);
            }
        }

        // The connection failed. Does nothing when that connection was already closed or replaced.
        private static void Drop(TcpClient client, string Reason)
        {
            lock (AnswerLock)
            {
                if (Client != client)
                {
                    return;
                }
                Client = null;
                Stream = null;
                Monitor.PulseAll(AnswerLock);
            }
            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
            GameManager.Instance.gameQueue.queueTask(() => ReportLost(Reason));
        }

        private static bool ReadExact(NetworkStream stream, byte[] Into, int Count)
        {
            int have = 0;
            while (have < Count)
            {
                int got = stream.Read(Into, have, Count - have);
                if (got <= 0)
                {
                    return false;
                }
                have += got;
            }
            return true;
        }

        private static void PutInt(byte[] Into, int At, int Value)
        {
            Into[At] = (byte)Value;
            Into[At + 1] = (byte)(Value >> 8);
            Into[At + 2] = (byte)(Value >> 16);
            Into[At + 3] = (byte)(Value >> 24);
        }

        private static int GetInt(byte[] From, int At)
        {
            return From[At] | (From[At + 1] << 8) | (From[At + 2] << 16) | (From[At + 3] << 24);
        }
    }
}
