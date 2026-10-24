using log4net;
using System.Reflection;

namespace Server
{
    public enum LogType
    {
        Server,
        Chat,
        Debug,
        Player,
        Spawn,
        Quests // quest V2 telemetry (log4net.config logger "Quests", lane F)
    }

    public class Logger
    {
        public static ILog GetLogger(LogType type = LogType.Server)
        {
            return LogManager.GetLogger(Assembly.GetEntryAssembly(), type.ToString());
        }
    }
}
