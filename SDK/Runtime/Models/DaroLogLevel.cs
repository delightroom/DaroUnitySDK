#nullable enable

namespace Daro
{
    /// <summary>
    /// SDK log verbosity. Verbose subsumes the Flutter SDK's
    /// <c>isDebugMode</c> boolean.
    /// </summary>
    public enum DaroLogLevel
    {
        None    = 0,
        Error   = 1,
        Warn    = 2,
        Info    = 3,
        Verbose = 4,
    }
}
