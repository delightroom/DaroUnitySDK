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
    /// into the native callback frame.
    /// </remarks>
    internal interface IDaroIosEventSink
    {
        void Loaded(string adUnitKey, DaroAdInfo info);
        void FailedToLoad(string adUnitKey, DaroAdLoadError error);
        void Shown(string adUnitKey, DaroAdInfo info);
        void FailedToShow(string adUnitKey, DaroAdDisplayError error);
        void Clicked(string adUnitKey, DaroAdInfo info);
        void Impression(string adUnitKey, DaroAdInfo info);
        void Hidden(string adUnitKey, DaroAdInfo info);
        void Dismissed(string adUnitKey, DaroAdInfo info);
        void EarnedReward(string adUnitKey, DaroAdInfo info, DaroRewardItem reward);
        void RevenuePaid(string adUnitKey, DaroAdInfo info, DaroRevenueInfo revenue);
        void Preloaded(string adUnitKey, DaroAdInfo info);
        void FailedToPreload(string adUnitKey, DaroAdLoadError error);
        void SdkInitialized();
        void SdkInitFailed(DaroSdkInitException ex);
    }

    /// <summary>
    /// Pure dispatch logic for the iOS native bridge's single JSON event
    /// channel.
    /// Lives outside <c>#if UNITY_IOS</c> so EditMode tests can drive it
    /// directly with synthetic JSON payloads — the iOS impl's
    /// <c>OnNativeEvent</c> static handler is a 1-line wrapper.
    /// </summary>
    /// <remarks>
    /// <para>Behavior contract (verified by <c>DaroIOSEventDispatcherTests</c>):</para>
    /// <list type="bullet">
    ///   <item>Unknown event names → silent drop (forward-compat with future shim additions).</item>
    ///   <item>Event for adUnitKey that <see cref="DaroAdInstanceRegistry"/> does not have →
    ///         silent drop (handles in-flight callbacks after <c>Destroy</c>).</item>
    ///   <item><c>__sdk__</c> sentinel → <see cref="IDaroIosEventSink.SdkInitialized"/> /
    ///         <see cref="IDaroIosEventSink.SdkInitFailed"/>; never goes through registry check.</item>
    ///   <item>Sink throw → caught + logged; subsequent dispatches in the same tick still run.</item>
    /// </list>
    /// </remarks>
    internal static class DaroIOSEventDispatcher
    {
        internal const string SdkSentinelAdUnitKey = "__sdk__";

        /// <summary>
        /// Parse <paramref name="eventJson"/> and forward into the sink.
        /// Never throws — malformed payloads / unknown events / unregistered
        /// adUnitKeys all degrade to silent drop.
        /// </summary>
        /// <summary>
        /// shim JSON 한 건에서 <see cref="DaroAdInfo"/> 를 만든다 — 이 디스패처 8곳과
        /// <c>DaroIOSNativeAdHandle</c> 4곳이 같은 키를 읽는다. 키가 늘거나 이름이 바뀌면 여기 한 곳이다.
        /// <c>mediationPlatform</c> · <c>adNetwork</c> 는 없으면 null.
        /// </summary>
        internal static DaroAdInfo ReadAdInfo(string eventJson, DaroAdFormat format, string adUnitKey) =>
            new DaroAdInfo(
                format, adUnitKey,
                DaroJsonHelpers.GetJsonDouble(eventJson, "latency"),
                DaroJsonHelpers.GetJsonString(eventJson, "mediationPlatform"),
                DaroJsonHelpers.GetJsonString(eventJson, "adNetwork"));

        internal static void Dispatch(string adUnitKey, string eventJson, IDaroIosEventSink sink)
        {
            if (sink == null) return;
            if (eventJson == null) return;

            string? evt = DaroJsonHelpers.GetJsonString(eventJson, "event");
            if (evt == null) return;

            // SDK lifecycle sentinel — bypasses registry routing.
            if (adUnitKey == SdkSentinelAdUnitKey)
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
            //
            // 모든 DaroAdInfo 가 `mediationPlatform` · `adNetwork` 키를 선택적으로 읽는다.
            // 키가 없으면 null. shim 이 adInfo 를 나르는 이벤트마다 두 키를 싣는다.
            // 이벤트마다 읽는 이유: iOS 는 Android 처럼 유닛별 프록시가 없어 로드 시점 값을 보관할
            // 자리가 없다 — 어느 이벤트에 싣는지는 shim 이 정한다.
            //
            // 배너·네이티브의 `adRevenuePaid` 만 두 키가 비어 온다. 수익 콜백은 revenue 하나만
            // 받는데(SDK 의 DaroAdRevenue 자체가 그렇다) 그 두 포맷은 수익이 로드/리프레시 버스트
            // 안에서 터져 adInfo 콜백과의 순서가 보장되지 않는다 — 기억해 둔 값을 실으면 새 노출이
            // 직전 낙찰 네트워크로 적힌다. 비는 편이 낫다는 판단이고, 그래서 null 로 온다.
            int adFormatInt = DaroJsonHelpers.GetJsonInt(eventJson, "adFormat", -1);
            if (!Enum.IsDefined(typeof(DaroAdFormat), adFormatInt)) return; // malformed
            var adFormat = (DaroAdFormat)adFormatInt;

            // Registry gate — dropped instances no-op.
            if (DaroAdInstanceRegistry.Find<object>(adFormat, adUnitKey) == null) return;

            switch (evt)
            {
                case "adLoaded":
                {
                    var info = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    Safely(() => sink.Loaded(adUnitKey, info));
                    break;
                }
                case "adFailedToLoad":
                {
                    var raw  = DaroJsonHelpers.GetJsonInt(eventJson, "errorCode");
                    var msg  = DaroJsonHelpers.GetJsonString(eventJson, "errorMessage") ?? string.Empty;
                    var err  = new DaroAdLoadError(DaroAdErrorCodeMapper.ToLoadErrorCode(raw), msg, adUnitKey, raw);
                    Safely(() => sink.FailedToLoad(adUnitKey, err));
                    break;
                }
                case "adShown":
                {
                    var info = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    Safely(() => sink.Shown(adUnitKey, info));
                    break;
                }
                case "adFailedToShow":
                {
                    var raw  = DaroJsonHelpers.GetJsonInt(eventJson, "errorCode");
                    var msg  = DaroJsonHelpers.GetJsonString(eventJson, "errorMessage") ?? string.Empty;
                    var err  = new DaroAdDisplayError(DaroAdErrorCodeMapper.ToDisplayErrorCode(raw), msg, raw);
                    Safely(() => sink.FailedToShow(adUnitKey, err));
                    break;
                }
                case "adClicked":
                {
                    var info = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    Safely(() => sink.Clicked(adUnitKey, info));
                    break;
                }
                case "adImpression":
                {
                    var info = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    Safely(() => sink.Impression(adUnitKey, info));
                    break;
                }
                case "adHidden":
                {
                    var info = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    Safely(() => sink.Hidden(adUnitKey, info));
                    break;
                }
                case "adDismissed":
                {
                    var info = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    Safely(() => sink.Dismissed(adUnitKey, info));
                    break;
                }
                case "earnedReward":
                {
                    var info   = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    var amount = DaroJsonHelpers.GetJsonInt(eventJson, "rewardAmount");
                    var type   = DaroJsonHelpers.GetJsonString(eventJson, "rewardType") ?? string.Empty;
                    var reward = new DaroRewardItem(amount, type);
                    Safely(() => sink.EarnedReward(adUnitKey, info, reward));
                    break;
                }
                case "adRevenuePaid":
                {
                    var info      = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    var micros    = DaroJsonHelpers.GetJsonLong(eventJson, "valueMicros");
                    var currency  = DaroJsonHelpers.GetJsonString(eventJson, "currencyCode") ?? "USD";
                    var precision = DaroJsonHelpers.GetJsonInt(eventJson, "precisionType");
                    var revenue   = DaroRevenueInfo.FromMicros(micros, currency, precision);
                    Safely(() => sink.RevenuePaid(adUnitKey, info, revenue));
                    break;
                }
                case "adPreloaded":
                {
                    var info = ReadAdInfo(eventJson, adFormat, adUnitKey);
                    Safely(() => sink.Preloaded(adUnitKey, info));
                    break;
                }
                case "adFailedToPreload":
                {
                    var raw  = DaroJsonHelpers.GetJsonInt(eventJson, "errorCode");
                    var msg  = DaroJsonHelpers.GetJsonString(eventJson, "errorMessage") ?? string.Empty;
                    var err  = new DaroAdLoadError(DaroAdErrorCodeMapper.ToLoadErrorCode(raw), msg, adUnitKey, raw);
                    Safely(() => sink.FailedToPreload(adUnitKey, err));
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
