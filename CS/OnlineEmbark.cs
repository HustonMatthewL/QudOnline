using System;
using XRL;
using XRL.CharacterBuilds;
using XRL.CharacterBuilds.Qud;

namespace QudOnline
{
    // Gives a new game the seed of the hub's world. An embark module without a window, like vanilla
    // QudSpecificBootHandlersModule; listed in EmbarkModules.xml. When no hub answers at the default address
    // the new game is an ordinary one.
    public class OnlineEmbark : AbstractEmbarkBuilderModule
    {
        private const int HubTimeout = 5000;

        // IDs are 32-bit: 2000 blocks of a million fit.
        private const int BlockSize = 1000000;
        private const int MaxBlock = 2000;

        public override void InitFromSeed(string seed)
        {
        }

        public override object handleBootEvent(string id, XRLGame game, EmbarkInfo info, object element = null)
        {
            if (id == QudGameBootModule.BOOTEVENT_GENERATESEEDS)
            {
                try
                {
                    UseHubSeed(game, info);
                }
                catch (Exception x)
                {
                    MetricsManager.LogException("QUDOnline::OnlineEmbark", x);
                }
            }
            return base.handleBootEvent(id, game, info, element);
        }

        // Runs just before the game stores info.GameSeed as its world seed (vanilla QudGameBootModule.SeedGame).
        private static void UseHubSeed(XRLGame Game, EmbarkInfo Info)
        {
            if (!OnlineLink.Connected
                && !OnlineLink.Connect(OnlineLink.DefaultHost, OnlineLink.DefaultPort, out string problem))
            {
                MetricsManager.LogInfo("[QUDOnline] No hub at " + OnlineLink.DefaultHost + ":" + OnlineLink.DefaultPort
                    + " (" + problem + "); this is an ordinary new game.");
                return;
            }
            if (!OnlineLink.Seed(HubTimeout, true, out string seed, out int block, out problem)
                || string.IsNullOrEmpty(seed))
            {
                MetricsManager.LogInfo("[QUDOnline] The hub gave no seed (" + problem
                    + "); this is an ordinary new game.");
                return;
            }
            Info.GameSeed = seed;
            Game.SetStringGameState(OnlineWorld.SeedState, seed);
            // Objects get their IDs from a counter in each game. Starting this game's counters in a block of
            // their own keeps IDs apart when zones travel between games.
            if (block > 0 && block <= MaxBlock)
            {
                Game.GameObjectIDSequence = block * BlockSize;
                Game.BodyPartIDSequence = block * BlockSize;
            }
            MetricsManager.LogInfo("[QUDOnline] New game in the hub's world, seed " + seed
                + ", ID block " + block + ".");
            OnlinePresence.Start();
            OnlineWorld.Start();
        }
    }
}
