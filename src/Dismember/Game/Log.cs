using MelonLoader;

namespace Dismember.Game
{
    /// <summary>Thread-safe logging through MelonLoader (its logger may be called from worker threads).</summary>
    internal static class Log
    {
        private static MelonLogger.Instance _logger;
        public static bool Verbose;

        public static void Init(MelonLogger.Instance logger) => _logger = logger;

        public static void Msg(string s) => _logger?.Msg(s);
        public static void Warn(string s) => _logger?.Warning(s);
        public static void Error(string s) => _logger?.Error(s);
        public static void Debug(string s)
        {
            if (Verbose) _logger?.Msg("[debug] " + s);
        }
    }
}
