# Qud Online

*A shared, persistent world for several players of [Caves of Qud](https://www.cavesofqud.com/). Work in progress.*

Every player runs a full, normal game. A small hub relays where everyone is and stores the zones of one shared
world. Each zone has an owner, the first player to arrive: the owner's game simulates the zone and stores it on
the hub. The other players in that zone see a live mirror of it, and what they do there (items dropped or picked
up, hits on creatures, walls and doors) is sent to the owner, who applies it. While several players share a zone,
time moves in rounds, so everyone gets a turn. The other players appear as real copies of their characters.

Built for game version 2.0.211. Scripts only; no Harmony patches.

**Status:** an early test build. It is only meant for two games on one machine or a trusted local network, and
it adds nothing to the game except the test wishes below.

## Running a test

1. Copy this folder into the game's `Mods` folder and enable **Qud Online** in the Mods menu. The `hub/` folder
   is not needed there.
   - Windows: `%USERPROFILE%\AppData\LocalLow\Freehold Games\CavesOfQud\Mods`
   - macOS: `~/Library/Application Support/com.FreeholdGames.CavesOfQud/Mods`
   - Linux: `~/.config/unity3d/Freehold Games/CavesOfQud/Mods`
2. Start the hub (Python 3, standard library only):

   ```sh
   python3 hub/online_hub.py                # listens on 127.0.0.1:7777
   python3 hub/online_hub.py --new-world    # forget the shared world and start a new one
   python3 hub/online_hub.py --selftest     # check the hub on its own
   ```

   It keeps the world in `hub/hub-store/`. `--host 0.0.0.0` lets other machines connect.
3. Start a **new game** while the hub runs on the same machine: it takes the hub's world seed (a new game started
   with no hub at 127.0.0.1:7777 is an ordinary one). Then wish `onlineConnect` (`onlineConnect:<host>:<port>`
   for a hub elsewhere).
4. Start a second game the same way. On macOS, `hub/second_game.sh` starts a second copy of the game next to the
   first one, logging to `Player2.log`. Load a different save in each copy.

## Wishes

| Wish | What it does |
|---|---|
| `onlineConnect`, `onlineConnect:<host>:<port>`, `onlineDisconnect` | Connect to the hub / disconnect. |
| `onlineWho` | List the other players and where they are. |
| `onlineSeed` | This game's world seed and the hub's. |
| `onlineZones` | How many zones the hub holds, and which zones in memory are out of date. |
| `onlineRole` | Whether you own the zone you stand in or are a guest, and how mirror updates went. |
| `onlineMirror:on\|off` | As a guest, apply the owner's updates or not. |
| `onlineRounds:on\|off` | Wait for the round to close after acting when others are in the zone, or not. |
| `onlineBackground`, `onlineBackground:on\|off` | Keep the game running while its window is not focused (on while connected). |
| `onlinePing` | Time five round trips to the hub. |
| `onlineSnapshot`, `onlineReload:<dir>`, `onlineReloadHere`, `onlineSendZone[:<dir>]` | Serializer tests: measure or rebuild a zone from its own snapshot. Use a spare save. |

## Safety

The hub has no accounts and no encryption, and games load the objects other players send them. Only connect to
a hub you run yourself, on a network you trust.

## Reporting a problem

Messages are written to the game's `Player.log` with the prefix `[QUDOnline]`, and errors with `QUDOnline::`.
The hub prints what it relays to its terminal.

## License

MIT; see [LICENSE](LICENSE). The license covers this mod's own code, not the game.
