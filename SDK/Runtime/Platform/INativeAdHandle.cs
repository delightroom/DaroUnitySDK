#nullable enable
using System;

namespace Daro.Internal
{
    /// <summary>
    /// Per-instance native ad handle. Created by
    /// <see cref="IDaroPlatform.CreateNativeAdHandle"/>; owned by exactly one
    /// <see cref="DaroNativeAd"/>. Disposing the <see cref="DaroNativeAd"/>
    /// disposes its handle.
    /// </summary>
    /// <remarks>
    /// Native ad uses an instance-owned pattern (vs. the platform-managed
    /// adUnitId-keyed dict used by other formats). N <see cref="DaroNativeAd"/>
    /// instances with the same adUnitId yield N independent handles + native
    /// loaders — required for list-UI use cases.
    /// </remarks>
    internal interface INativeAdHandle : IDisposable
    {
        /// <summary>
        /// Start loading. <paramref name="iconWidth"/> / <paramref name="iconHeight"/>
        /// are publisher-provided pixel hints — the platform shim sizes its
        /// off-screen host (and the underlying ImageView) to these so MAX's
        /// Glide loader requests an icon at the right resolution rather than
        /// a wasted-bandwidth oversized one.
        /// </summary>
        void Load(int iconWidth, int iconHeight);

        /// <summary>
        /// Slot path: called from <c>DaroNativeAdView.OnEnable</c>.
        /// Raw path: publisher invokes via <c>DaroNativeAd.NotifyVisible</c>.
        /// </summary>
        void NotifyVisible();

        /// <summary>
        /// Slot path: called from <c>DaroNativeAdView.OnDisable</c>.
        /// Raw path: publisher invokes via <c>DaroNativeAd.NotifyHidden</c>.
        /// </summary>
        void NotifyHidden();

        /// <summary>
        /// Triggers the SDK click pathway. v1 Android: publisher Button.onClick
        /// → this method (no native MAX click target — <c>registerClickableViews</c>
        /// is unused in v1, deferred to v2 follow-up).
        /// </summary>
        void NotifyClicked();
    }
}
