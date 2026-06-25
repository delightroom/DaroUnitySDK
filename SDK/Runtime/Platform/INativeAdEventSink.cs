#nullable enable

namespace Daro.Internal
{
    /// <summary>
    /// Event sink supplied by the owning <see cref="DaroNativeAd"/> to its
    /// <see cref="INativeAdHandle"/>. Methods are invoked on the Unity main
    /// thread — handles are responsible for marshaling via
    /// <see cref="MainThreadDispatcher"/> before calling.
    /// </summary>
    /// <remarks>
    /// Replaces the platform-level event slots used by other formats. Each
    /// <see cref="DaroNativeAd"/> owns its sink with a direct reference to
    /// itself, so callbacks route to the correct instance with no registry
    /// lookup.
    /// </remarks>
    internal interface INativeAdEventSink
    {
        void OnAdLoaded(DaroAdInfo adInfo, DaroNativeAdInfo nativeInfo);
        void OnAdFailedToLoad(DaroAdLoadError error);
        void OnAdImpression(DaroAdInfo adInfo);
        void OnAdClicked(DaroAdInfo adInfo);

        /// <summary>ILRD — net (fee-adjusted) revenue per paid impression.</summary>
        void OnAdRevenuePaid(DaroAdInfo adInfo, DaroRevenueInfo revenue);
    }
}
