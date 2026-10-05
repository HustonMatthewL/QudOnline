using System;
using XRL.Messages;

namespace QudOnline
{
    // Where the mod reports what it does: a grey line in the message log, mirrored to Player.log. Errors go to
    // Player.log with the prefix "QUDOnline::" and are shown in red.
    public static class OnlineLog
    {
        public static void Log(string Message)
        {
            MetricsManager.LogInfo("[QUDOnline] " + Message);
            MessageQueue.AddPlayerMessage("{{K|[Online] " + Message + "}}", Capitalize: false);
        }

        public static void Error(string Context, Exception x)
        {
            MetricsManager.LogException("QUDOnline::" + Context, x);
            MessageQueue.AddPlayerMessage("{{R|[Online ERROR] " + Context + ": " + x.GetType().Name + ": " + x.Message
                + "}}", Capitalize: false);
        }
    }
}
