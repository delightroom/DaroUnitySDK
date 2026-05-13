#nullable enable
using System;

namespace Daro.Internal
{
    /// <summary>
    /// Receives the parsed event 가지로 분기된 호출. <c>DaroIOSPlatform</c>
    /// implements this to forward into its 8 <c>Action</c> event slots and
    /// the pending init <c>TaskCompletionSource</c>.
    /// </summary>
    /// <remarks>
    /// Sink methods MAY throw — the dispatcher wraps every invocation in
    /// <c>try/catch</c> so a consumer-thrown handler never propagates back
    /// into the native callback frame (sketch reviewer-content WARN advisory).
    /// </remarks>
    internal interface IDaroIosEventSink
    {
        void Loaded(string adUnitId, DaroAdInfo info);
        void FailedToLoad(string adUnitId, DaroAdLoadError error);
        void Shown(string adUnitId, DaroAdInfo info);
        void FailedToShow(string adUnitId, DaroAdDisplayError error);
        void Clicked(string adUnitId, DaroAdInfo info);
        void Impression(string adUnitId, DaroAdInfo info);
        void Dismissed(string adUnitId, DaroAdInfo info);
        void EarnedReward(string adUnitId, DaroAdInfo info, DaroRewardItem reward);
        void SdkInitialized();
        void SdkInitFailed(DaroSdkInitException ex);
    }

    /// <summary>
    /// Pure dispatch logic for the iOS native bridge's single JSON event
    /// channel (sketch §"Event JSON Schema", §"OnNativeEvent dispatch logic").
    /// Lives outside <c>#if UNITY_IOS</c> so EditMode tests can drive it
    /// directly with synthetic JSON payloads — the iOS impl's
    /// <c>OnNativeEvent</c> static handler is a 1-line wrapper.
    /// </summary>
    /// <remarks>
    /// <para>Behavior contract (verified by <c>DaroIOSEventDispatcherTests</c>):</para>
    /// <list type="bullet">
    ///   <item>Unknown event names → silent drop (forward-compat with future shim additions).</item>
    ///   <item>Event for adUnitId that <see cref="DaroAdInstanceRegistry"/> does not have →
    ///         silent drop (handles in-flight callbacks after <c>Destroy</c>; sketch §"In-flight callback after Destroy").</item>
    ///   <item><c>__sdk__</c> sentinel → <see cref="IDaroIosEventSink.SdkInitialized"/> /
    ///         <see cref="IDaroIosEventSink.SdkInitFailed"/>; never goes through registry check.</item>
    ///   <item>Sink throw → caught + logged; subsequent dispatches in the same tick still run.</item>
    /// </list>
    /// </remarks>
    internal static class DaroIOSEventDispatcher
    {
        internal const string SdkSentinelAdUnitId = "__sdk__";

        /// <summary>
        /// Parse <paramref name="eventJson"/> and forward into the sink.
        /// Never throws — malformed payloads / unknown events / unregistered
        /// adUnitIds all degrade to silent drop.
        /// </summary>
        internal static void Dispatch(string adUnitId, string eventJson, IDaroIosEventSink sink)
        {
            if (sink == null) return;
            if (eventJson == null) return;

            string? evt = DaroJsonHelpers.GetJsonString(eventJson, "event");
            if (evt == null) return;

            // SDK lifecycle sentinel — bypasses registry routing.
            if (adUnitId == SdkSentinelAdUnitId)
            {
                if (evt == "sdkInitialized")
                {
                    Safely(sink.SdkInitialized);
                }
                else if (evt == "sdkInitFailed")
                {
                    var msg  = DaroJsonHelpers.GetJsonString(eventJson, "errorMessage") ?? string.Empty;
                    var code = DaroJsonHelpers.GetJsonInt(eventJson, "errorCode");
                    var ex   = new DaroSdkInitException(msg, code);
                    Safely(() => sink.SdkInitFailed(ex));
                }
                // unknown __sdk__ event → silent drop
                return;
            }

            // Per-instance event — adFormat decides routing + DaroAdInfo construction.
            int adFormatInt = DaroJsonHelpers.GetJsonInt(eventJson, "adFormat", -1);
            if (!Enum.IsDefined(typeof(DaroAdFormat), adFormatInt)) return; // malformed
            var adFormat = (DaroAdFormat)adFormatInt;

            // Registry gate — dropped instances no-op (sketch §"In-flight callback after Destroy").
            if (DaroAdInstanceRegistry.Find<object>(adFormat, adUnitId) == null) return;

            switch (evt)
            {
                case "adLoaded":
                {
                    var info = new DaroAdInfo(adFormat, adUnitId, DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    Safely(() => sink.Loaded(adUnitId, info));
                    break;
                }
                case "adFailedToLoad":
                {
                    var raw  = DaroJsonHelpers.GetJsonInt(eventJson, "errorCode");
                    var msg  = DaroJsonHelpers.GetJsonString(eventJson, "errorMessage") ?? string.Empty;
                    var err  = new DaroAdLoadError(DaroAdErrorCodeMapper.ToLoadErrorCode(raw), msg, adUnitId, raw);
                    Safely(() => sink.FailedToLoad(adUnitId, err));
                    break;
                }
                case "adShown":
                {
                    var info = new DaroAdInfo(adFormat, adUnitId, DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    Safely(() => sink.Shown(adUnitId, info));
                    break;
                }
                case "adFailedToShow":
                {
                    var raw  = DaroJsonHelpers.GetJsonInt(eventJson, "errorCode");
                    var msg  = DaroJsonHelpers.GetJsonString(eventJson, "errorMessage") ?? string.Empty;
                    var err  = new DaroAdDisplayError(DaroAdErrorCodeMapper.ToDisplayErrorCode(raw), msg, raw);
                    Safely(() => sink.FailedToShow(adUnitId, err));
                    break;
                }
                case "adClicked":
                {
                    var info = new DaroAdInfo(adFormat, adUnitId, DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    Safely(() => sink.Clicked(adUnitId, info));
                    break;
                }
                case "adImpression":
                {
                    var info = new DaroAdInfo(adFormat, adUnitId, DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    Safely(() => sink.Impression(adUnitId, info));
                    break;
                }
                case "adDismissed":
                {
                    var info = new DaroAdInfo(adFormat, adUnitId, DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    Safely(() => sink.Dismissed(adUnitId, info));
                    break;
                }
                case "earnedReward":
                {
                    var info   = new DaroAdInfo(adFormat, adUnitId, DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    var amount = DaroJsonHelpers.GetJsonInt(eventJson, "rewardAmount");
                    var type   = DaroJsonHelpers.GetJsonString(eventJson, "rewardType") ?? string.Empty;
                    var reward = new DaroRewardItem(amount, type);
                    Safely(() => sink.EarnedReward(adUnitId, info, reward));
                    break;
                }
                // unknown event → silent drop
            }
        }

        private static void Safely(Action call)
        {
            try { call(); }
            catch (Exception ex) { DaroLog.Exception("iOS", ex); }
        }
    }
}
