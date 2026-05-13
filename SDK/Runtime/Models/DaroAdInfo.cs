#nullable enable

namespace Daro
{
    /// <summary>
    /// Mirrors DaroObjCAdInfo from DaroSDK's native ObjC bridge.
    /// Fields are exactly those DaroSDK exposes publicly — we do not
    /// surface internal Swift fields (adNetwork, revenue, etc.)
    /// that never cross the plugin boundary.
    /// </summary>
    public sealed class DaroAdInfo
    {
        public DaroAdFormat AdFormat { get; }
        public string       AdUnitId { get; }

        /// <summary>
        /// Load latency in milliseconds; <c>null</c> if the native side
        /// did not report a value. Matches Daro's cross-platform contract
        /// (Android: <c>MaxAd.requestLatencyMillis</c>; iOS: <c>MAAd.requestLatency * 1000</c>).
        /// </summary>
        public double? Latency { get; }

        public DaroAdInfo(DaroAdFormat adFormat, string adUnitId, double? latency)
        {
            AdFormat = adFormat;
            AdUnitId = adUnitId;
            Latency  = latency;
        }
    }
}
