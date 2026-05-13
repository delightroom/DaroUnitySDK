#nullable enable

namespace Daro
{
    /// <summary>
    /// Mirrors DaroObjCAdFormat from DaroSDK's native ObjC bridge.
    /// Raw integer values are fixed across platforms so C# can cast
    /// directly from native payloads without a translation table.
    /// </summary>
    public enum DaroAdFormat
    {
        Banner       = 0,
        Interstitial = 1,
        Rewarded     = 2,
        Native       = 3,
        AppOpen      = 4,
        LightPopup   = 5,
    }
}
