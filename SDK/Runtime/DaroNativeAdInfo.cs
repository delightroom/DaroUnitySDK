#nullable enable
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Native ad asset payload populated by the platform handle and exposed
    /// to the publisher via <see cref="DaroNativeAd.Info"/>. Bind through
    /// <see cref="DaroNativeAdView"/> (slot path) or read fields directly
    /// (raw path, custom layout).
    /// </summary>
    /// <remarks>
    /// All fields nullable — not every ad supplies every slot. Field set
    /// matches daro-m's <c>DaroNativeAdBinder</c> exposed surface (5 view IDs:
    /// title / body / cta / icon / mediaGroup); advertiser / star-rating are
    /// absent because daro-m doesn't surface them through its public binder.
    /// <see cref="MediaImage"/> is always <c>null</c> on Android v1
    /// (image-only scope; video deferred).
    /// </remarks>
    public sealed class DaroNativeAdInfo
    {
        public string?    Title        { get; }
        public string?    Body         { get; }
        public string?    CallToAction { get; }
        public Texture2D? Icon         { get; }
        public Texture2D? MediaImage   { get; }

        /// <summary>
        /// Whether the SDK can deliver a click for this fill. When
        /// <c>false</c> (iOS only — Android/Editor always <c>true</c>), the
        /// underlying mediation network has wired its tap recognizer on a
        /// view hierarchy the overlay cannot cover, so taps will not produce
        /// <see cref="DaroNativeAd.OnAdClicked"/>. The impression and asset
        /// payload are still valid — accounting fires normally. Publishers
        /// who care about click attribution should hide the prefab when this
        /// flag is <c>false</c>:
        /// <code>if (!ad.Info.IsCtaInteractive) view.gameObject.SetActive(false);</code>
        /// See <c>docs/study/ios-native-ad-overlay-click-attribution.md</c>
        /// for the touch-ownership model that produces this signal.
        /// </summary>
        public bool       IsCtaInteractive { get; }

        public DaroNativeAdInfo(
            string?    title,
            string?    body,
            string?    callToAction,
            Texture2D? icon,
            Texture2D? mediaImage,
            bool       isCtaInteractive = true)
        {
            Title            = title;
            Body             = body;
            CallToAction     = callToAction;
            Icon             = icon;
            MediaImage       = mediaImage;
            IsCtaInteractive = isCtaInteractive;
        }
    }
}
