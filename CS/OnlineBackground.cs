using System;
using System.Threading;
using XRL;
using XRL.Core;

namespace QudOnline
{
    // Keeps a game that is not the focused window running while it is connected to a hub, so two games can be
    // watched side by side. Vanilla stops an unfocused game in several places (the wait at the top of the input
    // loop in XRLCore.PlayerTurn, the early return in GameManager.Update, Keyboard.kbhit), all of which look at
    // two public flags that GameManager.OnApplicationFocus clears; a small thread keeps setting them again.
    // Unity itself is told to keep running through Application.runInBackground, as the vanilla option
    // "Keep Caves of Qud active in background" does. Vanilla sets that back to false every time the options are
    // applied (Options.UpdateFlags), so it is asserted again once a second. Keys still reach only the focused
    // window.
    // The same thread notices when the game the hub knows about has ended, since no turn is taken any more then.
    public static class OnlineBackground
    {
        private const int Interval = 50;

        // Application.runInBackground is asserted every this many passes.
        private const int AssertPasses = 20;

        // Whether the player wants it; it only acts while connected.
        public static bool On = true;

        private static Thread Keeper;
        private static int Passes;

        // What runInBackground was before the mod touched it, and whether the mod holds it at true now.
        private static bool Before;
        private static bool Holding;

        // What the Unity thread last saw: runInBackground before and after the mod's assertion.
        private static string LastSeen = "not yet set";

        // For how many passes in a row the game the hub knows about has not been running.
        private static int Ended;
        private const int EndedPasses = 20;

        // The first error of each job is reported, later ones are not.
        private static bool FlagsFailed;
        private static bool UnityFailed;
        private static bool EndFailed;

        // A connection was made, or the wish changed On.
        public static void Start()
        {
            if (Keeper == null)
            {
                Keeper = new Thread(Keep);
                Keeper.IsBackground = true;
                Keeper.Name = "QUDOnline focus";
                Keeper.Start();
            }
        }

        public static string Describe()
        {
            return "Running in the background is " + (On ? "on" : "off")
                + ", the hub is " + (OnlineLink.Connected ? "connected" : "not connected")
                + ", Unity's run-in-background: " + LastSeen + ".";
        }

        // Three jobs, each on its own: an error in one must not stop the others.
        private static void Keep()
        {
            while (true)
            {
                bool wanted = false;
                try
                {
                    wanted = On && OnlineLink.Connected;
                    if (wanted)
                    {
                        XRLCore.bThreadFocus = true;
                        // The field, not the property: the property's setter clears the keys waiting to be read.
                        GameManager._focused = true;
                    }
                }
                catch (Exception x)
                {
                    if (!FlagsFailed)
                    {
                        FlagsFailed = true;
                        MetricsManager.LogException("QUDOnline::OnlineBackground focus flags", x);
                    }
                }
                try
                {
                    if (wanted ? (!Holding || Passes % AssertPasses == 0) : Holding)
                    {
                        bool hold = wanted;
                        GameManager.Instance.uiQueue.queueTask(() => RunInBackground(hold));
                    }
                    Passes++;
                }
                catch (Exception x)
                {
                    if (!UnityFailed)
                    {
                        UnityFailed = true;
                        MetricsManager.LogException("QUDOnline::OnlineBackground run in background", x);
                    }
                }
                try
                {
                    // A player whose game is over (death, back to the main menu) must not keep standing in the
                    // world or owning a zone. Loading takes a moment too, hence the second of patience.
                    if (OnlineLink.Connected && OnlinePresence.InWorld && (The.Game == null || !The.Game.Running))
                    {
                        if (++Ended >= EndedPasses)
                        {
                            Ended = 0;
                            OnlinePresence.LeaveWorld();
                        }
                    }
                    else
                    {
                        Ended = 0;
                    }
                }
                catch (Exception x)
                {
                    if (!EndFailed)
                    {
                        EndFailed = true;
                        MetricsManager.LogException("QUDOnline::OnlineBackground game end", x);
                    }
                }
                Thread.Sleep(Interval);
            }
        }

        // On the Unity thread.
        private static void RunInBackground(bool Hold)
        {
            bool was = UnityEngine.Application.runInBackground;
            if (Hold)
            {
                if (!Holding)
                {
                    Before = was;
                    Holding = true;
                }
                UnityEngine.Application.runInBackground = true;
                LastSeen = (was ? "was still on" : "had been switched off by the game") + ", now on";
            }
            else if (Holding)
            {
                Holding = false;
                UnityEngine.Application.runInBackground = Before;
                LastSeen = "given back to the game (" + (Before ? "on" : "off") + ")";
            }
        }
    }
}
