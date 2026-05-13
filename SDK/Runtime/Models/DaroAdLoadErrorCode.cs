#nullable enable

namespace Daro
{
    /// <summary>
    /// Values mirror DaroError.Code rawValue exactly (DaroSDK native).
    /// Only codes that can appear in a load-failure context are listed;
    /// any unlisted int coming from native maps to <see cref="Unspecified"/>.
    /// </summary>
    public enum DaroAdLoadErrorCode
    {
        Unspecified                = -1,
        NotInitialized             = -2,
        InitializationFailed       = -3,
        NoFill                     = 204,
        AdLoadFailed               = -5001,
        InvalidAdUnitIdentifier    = -5603,
        NetworkError               = -1000,
        NetworkTimeout             = -1001,
        NoNetwork                  = -1009,
        FullscreenAdAlreadyLoading = -26,
    }
}
