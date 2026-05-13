#nullable enable

namespace Daro
{
    /// <summary>
    /// Values mirror DaroError.Code rawValue exactly (DaroSDK native).
    /// Any unlisted int from native maps to <see cref="Unspecified"/>.
    /// </summary>
    public enum DaroAdDisplayErrorCode
    {
        Unspecified                       = -1,
        FullscreenAdAlreadyShowing        = -23,
        FullscreenAdNotReady              = -24,
        FullscreenAdInvalidViewController = -25,
        FullscreenAdLoadWhileShowing      = -27,
        NetworkError                      = -1000,
    }
}
