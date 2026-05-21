#nullable enable
using System;
using UnityEngine;

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

        /// <summary>
        /// Sync the publisher's CTA Button screen rect + touch-enabled state to
        /// the native overlay (iOS only; Android + Editor are no-ops). Coordinate
        /// space: Unity pixel space, origin bottom-left, no DPI division — the
        /// iOS shim converts to UIKit internally. <paramref name="touchEnabled"/>
        /// is the composite of all Unity interactability axes computed by the
        /// internal driver (see <c>DaroNativeCtaDriver</c>). Callers that bypass
        /// the helper-bound driver take ownership of every lifecycle transition.
        /// </summary>
        /// <remarks>
        /// Android + Editor implement as verbose-log no-ops for interface
        /// uniformity. See <c>docs/features/native-bridge.md</c> Native ad iOS
        /// section for the click-overlay architecture.
        /// </remarks>
        void SetCtaScreenRect(Rect rect, bool touchEnabled);

        /// <summary>
        /// Drop the CTA overlay rect and disable touch (frame intact; only the
        /// touch gate flips). Counterpart to <see cref="SetCtaScreenRect"/>.
        /// Called on publisher-driven teardown (UnwireCta / Unbind / Dispose /
        /// Button GameObject destroyed). Android + Editor: no-op.
        /// </summary>
        void ClearCtaScreenRect();
    }
}
