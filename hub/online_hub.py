#!/usr/bin/env python3
"""Test hub for the QUDOnline mod (kept in the mod's hub/ folder, which is not installed into the game).

  python3 online_hub.py                  listen on 127.0.0.1:7777
  python3 online_hub.py --host 0.0.0.0 --port 7777
  python3 online_hub.py --new-world      forget the shared world (seed and zones) and start a new one
  python3 online_hub.py --tick 0.025     length of a round's window in seconds
  python3 online_hub.py --selftest       start a hub, talk to it, report
  python3 online_hub.py --compare A B    say how two stored snapshots differ

A frame is: payload length (4 bytes, little-endian), message type (1 byte), request number (4 bytes), payload.
The hub answers every request with a REPLY or FAILURE frame carrying the same request number. Stored zones are
kept in memory and written to hub-store/ next to this script, in the packed form the mod sends (4-byte length,
then the snapshot gzipped).

The shared world lives in hub-store/world/: seed.txt and one file per zone, named by the zone's ID. A key with
no "|" in it is a zone of that world; other keys are test data and stay in hub-store/. GET_SEED returns the
world's seed and a block number, as "seed<TAB>block": a request with the payload "new" (a game being created)
gets the next unused block, any other request gets block 0. A game numbers its objects inside its own block, so
objects made by different games never share an ID. STORE_NEW stores a zone only if the world has none under
that key and answers "stored" or "kept". LIST_ZONES returns the world's keys, one per line. After a zone of the
world is stored, every other connection gets CHANGED with its key.

A zone of the world has one owner: the first player to CLAIM it ("game id<TAB>zone"; a claim also gives up the
zone claimed before, and an empty zone claims nothing). The reply names the owner; every player in the zone gets
ROLE with "zone<TAB>owner id<TAB>number of players" whenever that changes. When the owner leaves, the next in
order of arrival owns the zone. A FETCH of a zone owned by another player first sends that owner WANT with the
zone's key and waits up to WANT_WAIT seconds for it to store a fresh copy. A zone stored by its owner is passed
to the other players in it as MIRROR (same payload as the store) instead of CHANGED.

A FETCH of a zone the world does not have is answered with FAILURE, and the asker is noted as building it:
anyone else fetching that zone in the meantime waits up to BUILD_WAIT seconds for the builder to store it, so a
zone is never built twice at once. ACT ("key length, key, body", no answer) is passed to the owner of that zone:
it is how a guest tells the owner what it dropped or took.

Time in a zone with two or more players moves in rounds. A player that has just acted sends ROUND with the
zone's key. The first one opens a round; it closes after TICK seconds or as soon as every player in the zone has
sent ROUND, whichever comes first, and also when a player leaving makes that true. A ROUND sent with a request
number is answered when its round closes (at once when the sender is alone in the zone). When a round closes,
every player in the zone who did not send ROUND gets WAIT with the zone's key: they spend a turn too.

BODY ("key length, zone, then a copy of the sender's character") is kept as that connection's latest body and
passed to the other players in the zone; a player claiming a zone is sent the bodies of those already in it.

TELL ("ID length (1 byte), game id, body", no answer) is passed unchanged to the player with that game id,
wherever they are: it is how a game tells another player that their character was hurt.

Presence needs no answer (request number 0). A player says HERE with "id<TAB>name<TAB>zone<TAB>x<TAB>y" (more
fields may follow: hit points and their maximum); the hub
keeps the latest of each connection, passes it to the others, and gives a newcomer everyone's latest. When a
connection ends the others get GONE with the player's id.
"""
import argparse
import gzip
import os
import re
import socket
import socketserver
import struct
import sys
import threading
import time
import uuid

PING, STORE_ZONE, FETCH_ZONE, REPLY, FAILURE, HERE, GONE = 1, 2, 3, 4, 5, 6, 7
GET_SEED, STORE_NEW, LIST_ZONES, CHANGED = 8, 9, 10, 11
CLAIM, ROLE, WANT, MIRROR, ACT = 12, 13, 14, 15, 16
ROUND, WAIT, BODY, TELL = 17, 18, 19, 20
TICK = 0.025
WANT_WAIT = 4.0
BUILD_WAIT = 4.0
BUILD_CLAIM = 15.0
HEADER = struct.Struct("<IBI")
MAX_PAYLOAD = 64 * 1024 * 1024
STORE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hub-store")
WORLD_KEY = re.compile(r"^[A-Za-z0-9._-]+$")

store = {}
store_lock = threading.Lock()

clients = set()
clients_lock = threading.Lock()

members = {}                        # zone -> the connections in it, in order of arrival; the first owns it
members_lock = threading.Lock()
rounds = {}                         # zone -> the open round: who acted, who awaits an answer (guarded by members_lock)
round_count = {}                    # zone -> rounds closed so far
stores = {}                         # zone -> how many times it was stored, for those waiting on a fresh copy
building = {}                       # zone -> (the connection building it, when that claim lapses)
fresh = threading.Condition()           # guards stores and building


def say(text):
    print(time.strftime("%H:%M:%S ") + text, flush=True)


def read_exact(sock, count):
    data = bytearray()
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            return None
        data += chunk
    return bytes(data)


def read_frame(sock):
    header = read_exact(sock, HEADER.size)
    if header is None:
        return None
    length, kind, request = HEADER.unpack(header)
    if length > MAX_PAYLOAD:
        raise ValueError("frame of %d bytes" % length)
    payload = read_exact(sock, length) if length else b""
    if payload is None:
        return None
    return kind, request, payload


def write_frame(sock, kind, request, payload=b""):
    sock.sendall(HEADER.pack(len(payload), kind, request) + payload)


def world_dir():
    return os.path.join(STORE_DIR, "world")


def in_world(key):
    return WORLD_KEY.match(key) is not None


def file_for(key):
    if in_world(key):
        return os.path.join(world_dir(), key + ".bin")
    return os.path.join(STORE_DIR, re.sub(r"[^A-Za-z0-9._-]", "_", key) + ".bin")


def world_seed():
    path = os.path.join(world_dir(), "seed.txt")
    if os.path.exists(path):
        return open(path, encoding="utf-8").read().strip()
    os.makedirs(world_dir(), exist_ok=True)
    seed = str(uuid.uuid4())
    with open(path, "w", encoding="utf-8") as out:
        out.write(seed + "\n")
    return seed


def next_block():
    path = os.path.join(world_dir(), "blocks.txt")
    block = int(open(path).read().strip()) + 1 if os.path.exists(path) else 1
    with open(path, "w") as out:
        out.write("%d\n" % block)
    return block


def load_world():
    """Reads the stored zones of the world back into memory; returns how many."""
    count = 0
    if os.path.isdir(world_dir()):
        for name in os.listdir(world_dir()):
            if name.endswith(".bin") and in_world(name[:-4]):
                with open(os.path.join(world_dir(), name), "rb") as stored:
                    store[name[:-4]] = stored.read()
                count += 1
    return count


def new_world():
    if os.path.isdir(world_dir()):
        for name in os.listdir(world_dir()):
            if name.endswith(".bin") or name in ("seed.txt", "blocks.txt"):
                os.remove(os.path.join(world_dir(), name))
    for key in [k for k in store if in_world(k)]:
        del store[key]


def split_store(payload):
    (key_length,) = struct.unpack_from("<H", payload, 0)
    return payload[2:2 + key_length].decode("utf-8"), payload[2 + key_length:]


def keep(key, data):
    os.makedirs(os.path.dirname(file_for(key)), exist_ok=True)
    with open(file_for(key), "wb") as out:
        out.write(data)


def unpack(packed):
    (length,) = struct.unpack_from("<I", packed, 0)
    data = gzip.decompress(packed[4:])
    if len(data) != length:
        raise ValueError("packed snapshot says %d bytes, holds %d" % (length, len(data)))
    return data


class Handler(socketserver.BaseRequestHandler):
    def setup(self):
        self.send_lock = threading.Lock()
        self.player = None      # the id from this connection's HERE frames
        self.here = None        # its latest HERE payload
        self.name = ""          # the player's name from its HERE frames
        self.game = ""          # the id from its CLAIM frames
        self.zone = ""          # the zone it has claimed
        self.body = None        # its latest BODY payload

    def send(self, kind, request, payload=b""):
        with self.send_lock:
            write_frame(self.request, kind, request, payload)

    def handle(self):
        who = "%s:%d" % self.client_address
        say("connected: " + who)
        self.request.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        with clients_lock:
            clients.add(self)
        try:
            while True:
                frame = read_frame(self.request)
                if frame is None:
                    break
                kind, request, payload = frame
                self.answer(who, kind, request, payload)
        except (OSError, ValueError) as problem:
            say("%s: %s" % (who, problem))
        with clients_lock:
            clients.discard(self)
        self.announce(self.claim("")[1])
        with fresh:
            for key in [k for k, v in building.items() if v[0] is self]:
                del building[key]
            fresh.notify_all()
        if self.player is not None:
            self.tell_others(GONE, self.player.encode("utf-8"))
        say("disconnected: " + who)

    def claim(self, zone):
        """Moves this connection to a zone ("" for none); returns the id of that zone's owner and what changed."""
        changed = []
        with members_lock:
            if zone != self.zone:
                if self.zone:
                    left = members[self.zone]
                    left.remove(self)
                    if left:
                        changed.append((self.zone, left[0].game, list(left)))
                    else:
                        del members[self.zone]
                self.zone = zone
                if zone:
                    here = members.setdefault(zone, [])
                    here.append(self)
                    changed.append((zone, here[0].game, list(here)))
            owner = members[zone][0].game if zone else ""
            # A round must not wait for a player who has left.
            ready = [(name, rounds[name]) for name, _, present in changed
                     if name in rounds and all(member in rounds[name]["acted"] for member in present)]
            ready += [(name, rounds[name]) for name in rounds if name not in members]
        for name, current in ready:
            close_round(name, current)
        return owner, changed

    def join_round(self, who, zone, request):
        """This player has just acted in the zone."""
        with members_lock:
            present = members.get(zone, [])
            if self not in present or len(present) < 2:
                current = None
            else:
                current = rounds.get(zone)
                if current is None:
                    current = rounds[zone] = {"acted": [], "pending": [], "opened": time.time()}
                    timer = threading.Timer(TICK, close_round, (zone, current))
                    timer.daemon = True
                    timer.start()
                if self not in current["acted"]:
                    current["acted"].append(self)
                if request:
                    current["pending"].append((self, request))
                full = all(member in current["acted"] for member in present)
        if current is None:
            if request:
                self.send(REPLY, request)
        elif full:
            close_round(zone, current)

    def announce(self, changed):
        for name, owned_by, present in changed:
            say("%s: owner %s, %d player(s)" % (name, owned_by, len(present)))
            for member in present:
                try:
                    member.send(ROLE, 0, ("%s\t%s\t%d" % (name, owned_by, len(present))).encode("utf-8"))
                except OSError:
                    pass

    def pass_on_store(self, key, payload):
        """Tells the others about a stored zone: a mirror for those in it when its owner stored it."""
        with members_lock:
            present = list(members.get(key, []))
        mirrored = present if present and present[0] is self else []
        with clients_lock:
            others = [c for c in clients if c is not self]
        for other in others:
            try:
                if other in mirrored:
                    other.send(MIRROR, 0, payload)
                else:
                    other.send(CHANGED, 0, key.encode("utf-8"))
            except OSError:
                pass
        with fresh:
            stores[key] = stores.get(key, 0) + 1
            building.pop(key, None)
            fresh.notify_all()

    def wait_for_builder(self, who, key):
        """A fetch of a zone the world lacks: notes the asker as its builder, or waits for the one who is."""
        with fresh:
            builder = building.get(key)
            if builder is None or builder[0] is self or builder[1] < time.time():
                building[key] = (self, time.time() + BUILD_CLAIM)
                return
            deadline = time.time() + BUILD_WAIT
            while key in building and time.time() < deadline:
                fresh.wait(deadline - time.time())
        say("%s waited for %s to be built" % (who, key))

    def ask_owner(self, who, key):
        """Before a fetch: has the zone's owner, if it is someone else, store a fresh copy."""
        with members_lock:
            present = members.get(key, [])
            owner = present[0] if present and present[0] is not self else None
        if owner is None:
            return
        with fresh:
            before = stores.get(key, 0)
        try:
            owner.send(WANT, 0, key.encode("utf-8"))
        except OSError:
            return
        deadline = time.time() + WANT_WAIT
        with fresh:
            while stores.get(key, 0) == before and time.time() < deadline:
                fresh.wait(deadline - time.time())
            answered = stores.get(key, 0) != before
        say("%s wants %s fresh from its owner: %s"
            % (who, key, "got it" if answered else "no answer, using the stored copy"))

    def tell_others(self, kind, payload):
        with clients_lock:
            others = [c for c in clients if c is not self]
        for other in others:
            try:
                other.send(kind, 0, payload)
            except OSError:
                pass

    def answer(self, who, kind, request, payload):
        if kind == PING:
            self.send(REPLY, request)
            say("%s ping" % who)
        elif kind == HERE:
            fields = payload.decode("utf-8").split("\t")
            if len(fields) < 5:
                say("%s sent a malformed HERE" % who)
                return
            if self.player is not None and self.player != fields[0]:
                self.tell_others(GONE, self.player.encode("utf-8"))
            newcomer = self.here is None
            self.player, self.here, self.name = fields[0], payload, fields[1]
            if newcomer:
                with clients_lock:
                    known = [c.here for c in clients if c is not self and c.here is not None]
                for here in known:
                    self.send(HERE, 0, here)
            self.tell_others(HERE, payload)
            say("%s here: %s in %s at %s,%s" % (who, fields[1], fields[2], fields[3], fields[4]))
        elif kind == STORE_ZONE:
            key, data = split_store(payload)
            with store_lock:
                before = store.get(key)
                store[key] = data
                keep(key, data)
            self.send(REPLY, request)
            if in_world(key):
                self.pass_on_store(key, payload)
            say("%s stored %s (%d bytes%s)" % (who, key, len(data), "" if before is None else ", was %d" % len(before)))
        elif kind == STORE_NEW:
            key, data = split_store(payload)
            if not in_world(key):
                self.send(FAILURE, request, ("not a key of the world: " + key).encode("utf-8"))
                return
            with store_lock:
                is_new = key not in store
                if is_new:
                    store[key] = data
                    keep(key, data)
            self.send(REPLY, request, b"stored" if is_new else b"kept")
            if is_new:
                self.pass_on_store(key, payload)
            say("%s new zone %s (%d bytes): %s" % (who, key, len(data), "stored" if is_new else "already there, kept"))
        elif kind == TELL:
            target = payload[1:1 + payload[0]].decode("utf-8") if payload else ""
            with clients_lock:
                found = [c for c in clients if c is not self and c.game == target]
            for other in found:
                try:
                    other.send(TELL, 0, payload)
                except OSError:
                    pass
            say("%s tells %s (%d bytes)%s" % (who, target, len(payload), "" if found else ": not connected"))
        elif kind == BODY:
            key, data = split_store(payload)
            self.body = payload
            with members_lock:
                present = [m for m in members.get(key, []) if m is not self] if self.zone == key else []
            for member in present:
                try:
                    member.send(BODY, 0, payload)
                except OSError:
                    pass
            say("%s body (%d bytes) passed to %d player(s) in %s" % (who, len(data), len(present), key))
        elif kind == ROUND:
            self.join_round(who, payload.decode("utf-8"), request)
        elif kind == ACT:
            key, body = split_store(payload)
            with members_lock:
                present = members.get(key, [])
                owner = present[0] if present and present[0] is not self and self in present else None
            if owner is not None:
                try:
                    owner.send(ACT, 0, payload)
                except OSError:
                    pass
            say("%s acts in %s (%d bytes): %s"
                % (who, key, len(body), "passed to the owner" if owner else "no owner to tell"))
        elif kind == CLAIM:
            fields = payload.decode("utf-8").split("\t")
            if len(fields) != 2:
                self.send(FAILURE, request, b"a claim is: game id, tab, zone")
                return
            self.game = fields[0]
            owner, changed = self.claim(fields[1])
            self.send(REPLY, request, owner.encode("utf-8"))
            self.announce(changed)
            with members_lock:
                bodies = [m.body for m in members.get(fields[1], []) if m is not self and m.body is not None]
            for body in bodies:
                self.send(BODY, 0, body)
        elif kind == GET_SEED:
            with store_lock:
                seed = world_seed()
                block = next_block() if payload == b"new" else 0
            self.send(REPLY, request, ("%s\t%d" % (seed, block)).encode("utf-8"))
            say("%s asked for the seed%s" % (who, " for a new game, block %d" % block if block else ""))
        elif kind == LIST_ZONES:
            with store_lock:
                keys = sorted(k for k in store if in_world(k))
            self.send(REPLY, request, "\n".join(keys).encode("utf-8"))
            say("%s listed the world: %d zones" % (who, len(keys)))
        elif kind == FETCH_ZONE:
            key = payload.decode("utf-8")
            self.ask_owner(who, key)
            with store_lock:
                data = store.get(key)
            if data is None and in_world(key):
                self.wait_for_builder(who, key)
                with store_lock:
                    data = store.get(key)
            if data is None:
                self.send(FAILURE, request, ("nothing stored as " + key).encode("utf-8"))
                say("%s fetch %s: not stored" % (who, key))
            else:
                self.send(REPLY, request, data)
                say("%s fetched %s (%d bytes)" % (who, key, len(data)))
        else:
            self.send(FAILURE, request, ("unknown message type %d" % kind).encode("utf-8"))
            say("%s unknown message type %d" % (who, kind))


# The difference between two snapshots, as QUDOnline/CS/ZoneDelta.cs makes and applies it. The hub does not use
# it; it is here so the format has a second implementation that the self-test checks.
DELTA_BLOCK, DELTA_MULTIPLIER = 32, 1000003


def fnv(data):
    value = 2166136261
    for byte in data:
        value = ((value ^ byte) * 16777619) & 0xffffffff
    return value


def delta_number(value):
    out = bytearray()
    while value >= 128:
        out.append(value & 127 | 128)
        value >>= 7
    out.append(value)
    return out


def delta_hash(data, at):
    value = 0
    for k in range(DELTA_BLOCK):
        value = (value * DELTA_MULTIPLIER + data[at + k]) & 0xffffffff
    return value


def delta_make(old, new):
    out = bytearray(struct.pack("<IIII", len(old), fnv(old), len(new), fnv(new)))
    index = {}
    for i in range(0, len(old) - DELTA_BLOCK + 1, DELTA_BLOCK):
        index.setdefault(delta_hash(old, i), i)
    top = pow(DELTA_MULTIPLIER, DELTA_BLOCK - 1, 1 << 32)
    length, at, pending = len(new), 0, 0
    windowed = length >= DELTA_BLOCK
    window = delta_hash(new, 0) if windowed else 0
    while at < length:
        start = index.get(window) if windowed else None
        if start is not None and old[start:start + DELTA_BLOCK] == new[at:at + DELTA_BLOCK]:
            run = DELTA_BLOCK
            while start + run < len(old) and at + run < length and old[start + run] == new[at + run]:
                run += 1
            while at > pending and start > 0 and old[start - 1] == new[at - 1]:
                start, at, run = start - 1, at - 1, run + 1
            if at > pending:
                out += b"\x00" + delta_number(at - pending) + new[pending:at]
            out += b"\x01" + delta_number(start) + delta_number(run)
            at += run
            pending = at
            windowed = at + DELTA_BLOCK <= length
            if windowed:
                window = delta_hash(new, at)
        else:
            if windowed and at + DELTA_BLOCK < length:
                window = ((window - new[at] * top) * DELTA_MULTIPLIER + new[at + DELTA_BLOCK]) & 0xffffffff
            else:
                windowed = False
            at += 1
    if length > pending:
        out += b"\x00" + delta_number(length - pending) + new[pending:]
    return bytes(out)


def delta_apply(old, delta):
    old_length, old_hash, length, new_hash = struct.unpack_from("<IIII", delta, 0)
    if old_length != len(old) or old_hash != fnv(old):
        return None
    out, at = bytearray(), 16

    def number():
        nonlocal at
        value, shift = 0, 0
        while True:
            byte = delta[at]
            at += 1
            value |= (byte & 127) << shift
            if byte < 128:
                return value
            shift += 7

    while at < len(delta):
        kind = delta[at]
        at += 1
        if kind == 0:
            count = number()
            out += delta[at:at + count]
            at += count
        else:
            start = number()
            count = number()
            out += old[start:start + count]
    return bytes(out) if len(out) == length and fnv(out) == new_hash else None


def close_round(zone, current):
    """Ends a round, unless it has ended already: answers those who acted and reports it."""
    with members_lock:
        if rounds.get(zone) is not current:
            return
        del rounds[zone]
        number = round_count[zone] = round_count.get(zone, 0) + 1
        idle = [member for member in members.get(zone, []) if member not in current["acted"]]
    for member, request in current["pending"]:
        try:
            member.send(REPLY, request, str(number).encode("utf-8"))
        except OSError:
            pass
    for member in idle:
        try:
            member.send(WAIT, 0, zone.encode("utf-8"))
        except OSError:
            pass
    say("%s round %d (%d ms): acted %s; did not act %s" % (zone, number, (time.time() - current["opened"]) * 1000,
        ", ".join(member.name or member.game for member in current["acted"]) or "nobody",
        ", ".join(member.name or member.game for member in idle) or "nobody"))


class Hub(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def ask(sock, kind, request, payload=b""):
    write_frame(sock, kind, request, payload)
    return read_frame(sock)


def selftest():
    global STORE_DIR
    import tempfile
    STORE_DIR = tempfile.mkdtemp(prefix="hub-selftest-")
    hub = Hub(("127.0.0.1", 0), Handler)
    threading.Thread(target=hub.serve_forever, daemon=True).start()
    sock = socket.create_connection(hub.server_address)
    raw = os.urandom(2000) + b"zone " * 40000
    packed = struct.pack("<I", len(raw)) + gzip.compress(raw)
    key = "JoppaWorld.11.22.1.1.10|before"
    checks = [
        ("ping", ask(sock, PING, 1) == (REPLY, 1, b"")),
        ("store", ask(sock, STORE_ZONE, 2, struct.pack("<H", len(key)) + key.encode() + packed) == (REPLY, 2, b"")),
        ("fetch", ask(sock, FETCH_ZONE, 3, key.encode()) == (REPLY, 3, packed)),
        ("unpack", unpack(packed) == raw),
        ("file", open(file_for(key), "rb").read() == packed),
        ("fetch missing", ask(sock, FETCH_ZONE, 4, b"nowhere")[0] == FAILURE),
        ("unknown type", ask(sock, 99, 5)[0] == FAILURE),
    ]
    # The shared world: a seed that stays, first version of a zone wins, others hear of changes.
    zone = "JoppaWorld.11.22.1.2.10"
    stored = struct.pack("<H", len(zone)) + zone.encode() + packed
    other = struct.pack("<H", len(zone)) + zone.encode() + packed[:100]
    watcher = socket.create_connection(hub.server_address)
    watcher.settimeout(3)
    time.sleep(0.2)
    seed = ask(sock, GET_SEED, 6)
    try:
        checks += [
            ("seed", seed[0] == REPLY and len(seed[2]) > 8 and seed[2].endswith(b"\t0")
                and ask(sock, GET_SEED, 7)[2] == seed[2]),
            ("seed file", open(os.path.join(world_dir(), "seed.txt")).read().strip().encode() == seed[2][:-2]),
            ("new games get blocks of their own", ask(sock, GET_SEED, 18, b"new")[2] == seed[2][:-1] + b"1"
                and ask(sock, GET_SEED, 19, b"new")[2] == seed[2][:-1] + b"2"),
            ("empty world lists nothing", ask(sock, LIST_ZONES, 8) == (REPLY, 8, b"")),
            ("new zone is stored", ask(sock, STORE_NEW, 9, stored) == (REPLY, 9, b"stored")),
            ("others hear of a new zone", read_frame(watcher) == (CHANGED, 0, zone.encode())),
            ("second version is refused", ask(sock, STORE_NEW, 10, other) == (REPLY, 10, b"kept")),
            ("first version is kept", ask(sock, FETCH_ZONE, 11, zone.encode()) == (REPLY, 11, packed)),
            ("store replaces", ask(sock, STORE_ZONE, 12, other) == (REPLY, 12, b"")
                and ask(sock, FETCH_ZONE, 13, zone.encode()) == (REPLY, 13, packed[:100])),
            ("others hear of the change", read_frame(watcher) == (CHANGED, 0, zone.encode())),
            ("world lists its zones only", ask(sock, LIST_ZONES, 14) == (REPLY, 14, zone.encode())),
            ("world file", open(os.path.join(world_dir(), zone + ".bin"), "rb").read() == packed[:100]),
            ("test key refused as new zone",
                ask(sock, STORE_NEW, 15, struct.pack("<H", len(key)) + key.encode() + packed)[0] == FAILURE),
        ]
        store.clear()
        checks.append(("world is read back from disk", load_world() == 1 and store.get(zone) == packed[:100]))
        new_world()
        checks.append(("new world", ask(sock, LIST_ZONES, 16) == (REPLY, 16, b"")
            and ask(sock, GET_SEED, 17)[2] != seed[2]
            and ask(sock, GET_SEED, 20, b"new")[2].endswith(b"\t1")))
    except OSError as problem:
        checks.append(("world (%s)" % problem, False))
    watcher.close()
    sock.close()
    time.sleep(0.2)
    # Ownership: first in owns, guests get mirrors, a fetch asks the owner, the next in line takes over.
    global WANT_WAIT
    WANT_WAIT = 0.5
    a, b, c = (socket.create_connection(hub.server_address) for _ in range(3))
    for each in (a, b, c):
        each.settimeout(3)
    home = "JoppaWorld.11.22.1.1.10"
    keyed = struct.pack("<H", len(home)) + home.encode()
    try:
        checks.append(("first claim owns", ask(a, CLAIM, 1, ("A\t" + home).encode()) == (REPLY, 1, b"A")
            and read_frame(a) == (ROLE, 0, (home + "\tA\t1").encode())))
        checks.append(("owner's store with nobody else there", ask(a, STORE_ZONE, 2, keyed + b"v1") == (REPLY, 2, b"")
            and read_frame(b) == (CHANGED, 0, home.encode()) and read_frame(c) == (CHANGED, 0, home.encode())))
        # B fetches before entering: the hub asks A, A stores, B gets the fresh copy.
        write_frame(b, FETCH_ZONE, 3, home.encode())
        wanted = read_frame(a) == (WANT, 0, home.encode())
        write_frame(a, STORE_ZONE, 4, keyed + b"v2")
        checks.append(("fetch asks the owner first", wanted and read_frame(b) == (CHANGED, 0, home.encode())
            and read_frame(b) == (REPLY, 3, b"v2") and read_frame(a) == (REPLY, 4, b"")))
        read_frame(c)
        checks.append(("second claim is a guest", ask(b, CLAIM, 5, ("B\t" + home).encode()) == (REPLY, 5, b"A")
            and read_frame(b) == (ROLE, 0, (home + "\tA\t2").encode())
            and read_frame(a) == (ROLE, 0, (home + "\tA\t2").encode())))
        write_frame(a, STORE_ZONE, 6, keyed + b"v3")
        checks.append(("owner's store is mirrored to guests", read_frame(b) == (MIRROR, 0, keyed + b"v3")
            and read_frame(c) == (CHANGED, 0, home.encode()) and read_frame(a) == (REPLY, 6, b"")))
        # C fetches while A does not answer: the stored copy after the wait.
        began = time.time()
        write_frame(c, FETCH_ZONE, 7, home.encode())
        checks.append(("silent owner: stored copy after the wait", read_frame(c) == (REPLY, 7, b"v3")
            and time.time() - began >= 0.4
            and read_frame(a) == (WANT, 0, home.encode())))
        checks.append(("owner leaving hands over", ask(a, CLAIM, 8, b"A\t") == (REPLY, 8, b"")
            and read_frame(b) == (ROLE, 0, (home + "\tB\t1").encode())))
        checks.append(("owner fetching its own zone is not asked",
            ask(b, FETCH_ZONE, 9, home.encode()) == (REPLY, 9, b"v3")))
        ask(c, CLAIM, 10, ("C\t" + home).encode())
        read_frame(c)
        read_frame(b)
        b.close()
        checks.append(("owner disconnecting hands over", read_frame(c) == (ROLE, 0, (home + "\tC\t1").encode())))
    except OSError as problem:
        checks.append(("ownership (%s)" % problem, False))
    # A zone being built: the second asker waits for the first to store it. A guest's act reaches the owner.
    global BUILD_WAIT
    BUILD_WAIT = 2.0
    fresh_zone = "JoppaWorld.11.22.2.2.10"
    try:
        checks.append(("first asker is told to build", ask(a, FETCH_ZONE, 20, fresh_zone.encode())[0] == FAILURE))
        write_frame(c, FETCH_ZONE, 21, fresh_zone.encode())
        time.sleep(0.3)
        write_frame(a, STORE_NEW, 22, struct.pack("<H", len(fresh_zone)) + fresh_zone.encode() + b"built")
        got = [read_frame(c), read_frame(c)]
        checks.append(("second asker waits for the builder", (REPLY, 21, b"built") in got
            and (CHANGED, 0, fresh_zone.encode()) in got
            and read_frame(a) == (REPLY, 22, b"stored")))
        ask(a, CLAIM, 23, ("A\t" + fresh_zone).encode())
        read_frame(a)
        act = struct.pack("<H", len(fresh_zone)) + fresh_zone.encode() + b"\x01drop"
        write_frame(c, ACT, 0, act)
        time.sleep(0.2)
        ask(c, CLAIM, 24, ("C\t" + fresh_zone).encode())
        read_frame(c)
        checks.append(("an outsider's act is not passed on",
            read_frame(a) == (ROLE, 0, (fresh_zone + "\tA\t2").encode())))
        write_frame(c, ACT, 0, act)
        checks.append(("a guest's act reaches the owner", read_frame(a) == (ACT, 0, act)))
        # Rounds: a and c are both in fresh_zone here.
        global TICK
        TICK = 0.4
        began = time.time()
        lone = ask(a, ROUND, 30, fresh_zone.encode())
        checks.append(("a lone actor's round closes after the window", lone[:2] == (REPLY, 30)
            and 0.3 <= time.time() - began < 1))
        checks.append(("whoever did not act is told to wait", read_frame(c) == (WAIT, 0, fresh_zone.encode())))
        began = time.time()
        write_frame(a, ROUND, 31, fresh_zone.encode())
        write_frame(c, ROUND, 32, fresh_zone.encode())
        checks.append(("everyone acting closes the round early", read_frame(a)[:2] == (REPLY, 31)
            and read_frame(c)[:2] == (REPLY, 32)
            and time.time() - began < 0.3))
        began = time.time()
        write_frame(a, ROUND, 33, fresh_zone.encode())
        time.sleep(0.05)
        ask(c, CLAIM, 34, b"C\t")
        got = [read_frame(a)[:2], read_frame(a)[:2]]
        checks.append(("a player leaving closes the round", (REPLY, 33) in got and (ROLE, 0) in got
            and time.time() - began < 0.3))
        began = time.time()
        checks.append(("alone in a zone there is no round", ask(a, ROUND, 35, fresh_zone.encode()) == (REPLY, 35, b"")
            and time.time() - began < 0.2))
        # Bodies: a is alone in fresh_zone, c is in no zone.
        body_a = struct.pack("<H", len(fresh_zone)) + fresh_zone.encode() + b"\x01Abody-of-a"
        body_c = struct.pack("<H", len(fresh_zone)) + fresh_zone.encode() + b"\x01Cbody-of-c"
        write_frame(a, BODY, 0, body_a)
        time.sleep(0.2)
        claimed = ask(c, CLAIM, 40, ("C\t" + fresh_zone).encode())
        got = [read_frame(c), read_frame(c)]
        checks.append(("a newcomer is sent the bodies of those present", claimed[:2] == (REPLY, 40)
            and (BODY, 0, body_a) in got
            and read_frame(a)[0] == ROLE))
        write_frame(c, BODY, 0, body_c)
        checks.append(("a body is passed to the others in the zone", read_frame(a) == (BODY, 0, body_c)))
        ask(c, CLAIM, 41, b"C\t")
        read_frame(a)
        write_frame(a, TELL, 0, b"\x01Couch")
        checks.append(("a player is told by game id", read_frame(c) == (TELL, 0, b"\x01Couch")))
        checks.append(("nobody waits when all acted or after leaving", ask(c, PING, 36) == (REPLY, 36, b"")
            and ask(a, PING, 37) == (REPLY, 37, b"")))
    except OSError as problem:
        checks.append(("building, acts and rounds (%s)" % problem, False))
    for each in (a, c):
        each.close()
    time.sleep(0.2)
    # Two players: each learns of the other, and of the other leaving.
    one, two = socket.create_connection(hub.server_address), socket.create_connection(hub.server_address)
    one.settimeout(3)
    two.settimeout(3)
    here_one, here_two = ("id-1\tAda\tJoppaWorld.11.22.1.1.10\t37\t22".encode(),
        "id-2\tBo\tJoppaWorld.11.22.1.1.10\t40\t20".encode())
    try:
        write_frame(one, HERE, 0, here_one)
        time.sleep(0.2)
        write_frame(two, HERE, 0, here_two)
        checks.append(("newcomer is told who is here", read_frame(two) == (HERE, 0, here_one)))
        checks.append(("others are told of the newcomer", read_frame(one) == (HERE, 0, here_two)))
        moved = here_two.replace(b"40", b"41")
        write_frame(two, HERE, 0, moved)
        checks.append(("moves are passed on", read_frame(one) == (HERE, 0, moved)))
        longer = moved + b"\t17\t20"
        write_frame(two, HERE, 0, longer)
        checks.append(("presence may carry hit points", read_frame(one) == (HERE, 0, longer)))
        two.close()
        checks.append(("leaving is passed on", read_frame(one) == (GONE, 0, b"id-2")))
    except OSError as problem:
        checks.append(("presence (%s)" % problem, False))
    one.close()
    hub.shutdown()
    # The difference between two snapshots: made, applied, and refused for the wrong base.
    base = os.urandom(5000) + b"wall " * 3000 + os.urandom(3000)
    newer = base[:2000] + b"moved" + base[2000:9000] + os.urandom(40) + base[9100:] + b"tail"
    made = delta_make(base, newer)
    checks.append(("difference rebuilds the newer snapshot", delta_apply(base, made) == newer
        and len(made) < len(newer) // 10))
    checks.append(("difference is refused for another base", delta_apply(base[1:], made) is None))
    checks.append(("difference of tiny and empty data", delta_apply(b"", delta_make(b"", b"abc")) == b"abc"
        and delta_apply(base, delta_make(base, b"")) == b"" and delta_apply(base, delta_make(base, base)) == base))
    stored = [os.path.join(os.path.dirname(os.path.abspath(__file__)), "hub-store",
        "JoppaWorld.11.22.1.1.10_%s.bin" % n) for n in ("before", "after")]
    if all(os.path.exists(path) for path in stored):
        first, second = (unpack(open(path, "rb").read()) for path in stored)
        made = delta_make(first, second)
        print("      stored Joppa: whole %d bytes packed, difference %d bytes packed"
            % (len(gzip.compress(second)), len(gzip.compress(made))))
        checks.append(("difference of two stored Joppa snapshots", delta_apply(first, made) == second))
    for name, passed in checks:
        print(("ok    " if passed else "FAIL  ") + name)
    return all(passed for _, passed in checks)


def strings_in(data):
    counts = {}
    for found in re.findall(rb"[\x20-\x7e]{4,}", data):
        counts[found] = counts.get(found, 0) + 1
    return counts


def compare(path_a, path_b):
    a, b = unpack(open(path_a, "rb").read()), unpack(open(path_b, "rb").read())
    print("A: %d bytes   B: %d bytes   difference %+d" % (len(a), len(b), len(b) - len(a)))
    if a == b:
        print("identical")
        return
    first = next((i for i in range(min(len(a), len(b))) if a[i] != b[i]), min(len(a), len(b)))
    print("first difference at byte %d" % first)
    sa, sb = strings_in(a), strings_in(b)
    changed = sorted((sb.get(s, 0) - sa.get(s, 0), s) for s in set(sa) | set(sb) if sa.get(s, 0) != sb.get(s, 0))
    print("text runs whose count differs (B minus A):")
    for delta, text in (changed[:40] + ([] if len(changed) <= 80 else [(0, b"...")]) + changed[-40:]
            if len(changed) > 40 else changed):
        print("  %+d  %s" % (delta, text.decode("ascii")[:120]))


def main():
    global TICK
    parser = argparse.ArgumentParser(description="Test hub for the QUDOnline mod.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7777)
    parser.add_argument("--tick", type=float, default=TICK, help="length of a round's window in seconds")
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--new-world", action="store_true", help="forget the shared world and start a new one")
    parser.add_argument("--compare", nargs=2, metavar=("A", "B"))
    args = parser.parse_args()
    if args.selftest:
        sys.exit(0 if selftest() else 1)
    if args.compare:
        compare(*args.compare)
        return
    TICK = args.tick
    if args.new_world:
        new_world()
        say("the old world is forgotten")
    zones = load_world()
    hub = Hub((args.host, args.port), Handler)
    say("hub listening on %s:%d, storing zones in %s (Ctrl-C to stop)" % (args.host, args.port, STORE_DIR))
    say("world seed %s, %d zones stored" % (world_seed(), zones))
    try:
        hub.serve_forever()
    except KeyboardInterrupt:
        say("stopped")


if __name__ == "__main__":
    main()
