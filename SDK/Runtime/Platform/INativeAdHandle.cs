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
    /// adUnitKey-keyed dict used by other formats). N <see cref="DaroNativeAd"/>
    /// instances with the same adUnitKey yield N independent handles + native
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
        /// 뷰 없이 광고 한 개를 받아 둔다. 다음 <see cref="Load"/> 가 한 번 넘겨받아 에셋을 낸다
        /// ( 로딩 중이면 결과 대기, 실패·만료면 일반 로드 ). 결과는
        /// <see cref="INativeAdEventSink.OnAdPreloaded"/> /
        /// <see cref="INativeAdEventSink.OnAdFailedToPreload"/> 로 간다.
        /// </summary>
        void Preload();

        bool SupportsNativeMedia { get; }
        void SetMediaScreenRect(Rect rect, bool visible, bool touchEnabled);
        void ClearMediaScreenRect();

        void ConfigureAdChoices(DaroAdChoicesPosition position);
        void SetAdChoicesScreenRect(Rect rect, bool visible);
        void ClearAdChoicesScreenRect();

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
        /// uniformity.
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
