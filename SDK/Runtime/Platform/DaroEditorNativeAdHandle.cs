#nullable enable
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// In-Editor mock implementation of <see cref="INativeAdHandle"/>.
    /// Simulates load latency via a coroutine on <see cref="MainThreadDispatcher"/>;
    /// fires events through the supplied <see cref="INativeAdEventSink"/>.
    /// </summary>
    /// <remarks>
    /// <para>Multi-instance: each <c>DaroNativeAd</c> creates its own handle, with
    /// its own coroutine + own mock asset. N concurrent handles for the same
    /// <c>adUnitKey</c> all run independently — no shared state.</para>
    ///
    /// <para>Settings snapshot pattern (mirrors <c>DaroEditorPlatform.LoadCoroutine</c>):
    /// settings are captured at coroutine start so mid-delay Inspector tweaks don't
    /// desync a single load cycle.</para>
    ///
    /// <para>Dispose-race defense: <see cref="_disposed"/> is checked at coroutine
    /// resume (after yield) and inside every event-emitting closure
    /// (<see cref="MainThreadDispatcher.Enqueue(System.Action)"/> drain). After
    /// <see cref="Dispose"/> the coroutine self-cancels at the next yield resume.</para>
    /// </remarks>
    internal sealed class DaroEditorNativeAdHandle : INativeAdHandle
    {
        private readonly string             _adUnitKey;
        private readonly INativeAdEventSink _sink;
        private readonly DaroEditorSettings _settings;
        private bool _disposed;
        private bool _loaded;

        // Preload mock — 한 개를 받아 두고 다음 Load 가 한 번 넘겨받는다 ( DaroEditorPlatform 의 배너와 같은 규칙 ).
        private DaroEditorPreloadPhase _preloadPhase;
        private int   _preloadGeneration;
        private float _preloadedAtRealtime;
        private int   _preloadLatencyMs;
        private int   _claimedPreloadGeneration;   // Load 가 가져간 로딩 중 preload ( 0 = 없음 )

        /// <summary>Load 가 preload 를 넘겨받은 횟수 ( 받아 둔 것 + 로딩 중 연결 ). 테스트 관찰용.</summary>
        internal int PreloadConsumeCount { get; private set; }
        private readonly List<CtaRectCall> _ctaRectCalls = new();

        internal IReadOnlyList<CtaRectCall> CtaRectCalls => _ctaRectCalls;
        internal int ClearCtaScreenRectCallCount { get; private set; }

        internal DaroEditorNativeAdHandle(
            string adUnitKey,
            INativeAdEventSink sink,
            DaroEditorSettings settings)
        {
            _adUnitKey = adUnitKey;
            _sink     = sink;
            _settings = settings;
        }

        internal DaroAdChoicesPosition? AdChoicesPosition { get; private set; }
        internal Rect AdChoicesRect { get; private set; }
        internal bool AdChoicesVisible { get; private set; }
        internal int AdChoicesSyncCount { get; private set; }
        public bool SupportsNativeMedia { get; internal set; }
        internal Rect MediaRect { get; private set; }
        internal bool MediaVisible { get; private set; }
        internal bool MediaTouchEnabled { get; private set; }
        public void SetMediaScreenRect(Rect rect, bool visible, bool touchEnabled)
        {
            if (_disposed) return;
            MediaRect = rect;
            MediaVisible = visible;
            MediaTouchEnabled = visible && touchEnabled;
        }
        public void ClearMediaScreenRect()
        {
            MediaRect = default;
            MediaVisible = MediaTouchEnabled = false;
        }

        public void ConfigureAdChoices(DaroAdChoicesPosition position) => AdChoicesPosition = position;
        public void SetAdChoicesScreenRect(Rect rect, bool visible)
        {
            if (_disposed) return;
            AdChoicesRect = rect;
            AdChoicesVisible = visible;
            AdChoicesSyncCount++;
        }
        public void ClearAdChoicesScreenRect() => AdChoicesVisible = false;

        public void Load(int iconWidth, int iconHeight)
        {
            // iconWidth/iconHeight ignored in mock — Editor doesn't run MAX/Glide
            // sizing logic; mock asset is hard-coded 64×64 magenta. Real
            // dimension flow exercised on device via DaroAndroidNativeAdHandle.
            DaroLog.Verbose("Native", $"Handle[Editor].Load adUnit='{_adUnitKey}' icon={iconWidth}x{iconHeight} disposed={_disposed}");
            if (_disposed) return;
            MainThreadDispatcher.EnsureCreated();

            switch (_preloadPhase)
            {
                case DaroEditorPreloadPhase.Loaded:
                    _preloadPhase = DaroEditorPreloadPhase.None;
                    if (IsPreloadExpired()) break;          // 만료 → 일반 로드
                    PreloadConsumeCount++;
                    DeliverLoaded(_preloadLatencyMs);       // 받아 둔 것은 지연 없이 넘긴다
                    return;

                case DaroEditorPreloadPhase.Loading:
                    // 결과를 기다린다 — SimulatePreload 가 성공이면 에셋을 내고, 실패면 일반 로드로 넘긴다.
                    // 가져간 것은 더 이상 "들고 있는 preload" 가 아니다: 기기처럼 다음 Preload 가 새로 받는다.
                    _preloadPhase             = DaroEditorPreloadPhase.None;
                    _claimedPreloadGeneration = _preloadGeneration;
                    PreloadConsumeCount++;
                    return;

                default:
                    _preloadPhase = DaroEditorPreloadPhase.None;   // 실패 기록은 한 번 쓰고 지운다
                    break;
            }

            MainThreadDispatcher.RunCoroutine(SimulateLoad());
        }

        public void Preload()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].Preload adUnit='{_adUnitKey}' disposed={_disposed} phase={_preloadPhase}");
            if (_disposed) return;
            // 로딩 중이거나 TTL 안의 광고를 들고 있으면 무시한다. 만료·실패·소비 뒤에는 다시 받는다.
            if (_preloadPhase == DaroEditorPreloadPhase.Loading) return;
            if (_preloadPhase == DaroEditorPreloadPhase.Loaded && !IsPreloadExpired()) return;

            _preloadPhase = DaroEditorPreloadPhase.Loading;
            MainThreadDispatcher.EnsureCreated();
            MainThreadDispatcher.RunCoroutine(SimulatePreload(++_preloadGeneration));
        }

        private bool IsPreloadExpired() =>
            Time.realtimeSinceStartup - _preloadedAtRealtime > _settings.preloadTtlSeconds;

        private IEnumerator SimulatePreload(int preloadGeneration)
        {
            // Snapshot settings so mid-delay Inspector tweaks don't desync this run.
            var delay        = _settings.loadDelaySeconds;
            var successRate  = _settings.loadSuccessRate;
            var latencyMs    = _settings.loadLatencyMs;
            var errorCode    = _settings.loadErrorCode;
            var errorMessage = _settings.loadErrorMessage ?? string.Empty;

            if (delay > 0f)
                yield return new WaitForSecondsRealtime(delay);

            if (_disposed) yield break;

            // 로딩 중에 Load 가 가져갔는가 ( 핸드오프 ). 그렇다면 결과를 들고 있지 않고 넘긴다.
            // 가져간 요청은 _preloadPhase 를 건드리지 않는다 — 그사이 다음 Preload 가 시작됐을 수 있다.
            var claimed = _claimedPreloadGeneration == preloadGeneration;
            if (claimed) _claimedPreloadGeneration = 0;
            else if (_preloadGeneration != preloadGeneration) yield break;

            if (DaroEditorMockProbability.RollSuccess(successRate))
            {
                if (!claimed)
                {
                    _preloadPhase        = DaroEditorPreloadPhase.Loaded;
                    _preloadedAtRealtime = Time.realtimeSinceStartup;
                    _preloadLatencyMs    = latencyMs;
                }

                double? latency = latencyMs < 0 ? (double?)null : latencyMs;
                var adInfo = new DaroAdInfo(DaroAdFormat.Native, _adUnitKey, latency);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_disposed) return;
                    _sink.OnAdPreloaded(adInfo);
                });

                if (claimed) DeliverLoaded(latencyMs);
            }
            else
            {
                if (!claimed) _preloadPhase = DaroEditorPreloadPhase.Failed;

                var mapped = DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode);
                var err    = new DaroAdLoadError(mapped, errorMessage, _adUnitKey, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_disposed) return;
                    _sink.OnAdFailedToPreload(err);
                });

                // 가져간 뒤의 실패는 일반 로드로 폴백한다.
                if (claimed) MainThreadDispatcher.RunCoroutine(SimulateLoad());
            }
        }

        // 로드 성공의 마무리. 일반 로드와 preload 소비가 같이 쓴다.
        private void DeliverLoaded(int latencyMs)
        {
            _loaded = true;

            // -1 sentinel → null latency; positive → millis as-is.
            double? latency = latencyMs < 0 ? (double?)null : latencyMs;
            var adInfo     = new DaroAdInfo(DaroAdFormat.Native, _adUnitKey, latency);
            var nativeInfo = BuildMockNativeAdInfo(_adUnitKey);

            MainThreadDispatcher.Enqueue(() =>
            {
                if (_disposed) return;
                _sink.OnAdLoaded(adInfo, nativeInfo);
            });
        }

        private IEnumerator SimulateLoad()
        {
            // Snapshot settings so mid-delay Inspector tweaks don't desync this run.
            var delay        = _settings.loadDelaySeconds;
            var successRate  = _settings.loadSuccessRate;
            var latencyMs    = _settings.loadLatencyMs;
            var errorCode    = _settings.loadErrorCode;
            var errorMessage = _settings.loadErrorMessage ?? string.Empty;

            if (delay > 0f)
                yield return new WaitForSecondsRealtime(delay);

            if (_disposed) yield break;

            var success = DaroEditorMockProbability.RollSuccess(successRate);
            if (success)
            {
                DeliverLoaded(latencyMs);
            }
            else
            {
                var mapped = DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode);
                var err    = new DaroAdLoadError(mapped, errorMessage, _adUnitKey, errorCode);

                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_disposed) return;
                    _sink.OnAdFailedToLoad(err);
                });
            }
        }

        public void NotifyVisible()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].NotifyVisible adUnit='{_adUnitKey}' disposed={_disposed} loaded={_loaded}");
            if (_disposed || !_loaded) return;
            // Mock impression: fire only when a load has succeeded; matches
            // device behavior where MAX revenue listener requires a loaded ad.
            // Revenue follows the impression — device contract reports revenue
            // at didPayRevenue (= impression) time.
            var info    = new DaroAdInfo(DaroAdFormat.Native, _adUnitKey, latency: null);
            var revenue = DaroRevenueInfo.FromMicros(
                _settings.revenueValueMicros,
                _settings.revenueCurrencyCode ?? "USD",
                _settings.revenuePrecisionType);
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_disposed) return;
                _sink.OnAdImpression(info);
                _sink.OnAdRevenuePaid(info, revenue);
            });
        }

        public void NotifyHidden()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].NotifyHidden adUnit='{_adUnitKey}' (mock no-op)");
            // Mock: no-op — visibility-hidden doesn't affect impression accounting.
        }

        public void NotifyClicked()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].NotifyClicked adUnit='{_adUnitKey}' disposed={_disposed} loaded={_loaded}");
            if (_disposed || !_loaded) return;
            var info = new DaroAdInfo(DaroAdFormat.Native, _adUnitKey, latency: null);
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_disposed) return;
                _sink.OnAdClicked(info);
            });
        }

        // CTA overlay sync. Editor mock has no overlay concept; the
        // mock click path stays through NotifyClicked above. Verbose log only
        // for cross-platform interface uniformity + smoke-detection parity.
        public void SetCtaScreenRect(UnityEngine.Rect rect, bool touchEnabled)
        {
            _ctaRectCalls.Add(new CtaRectCall(rect, touchEnabled));
            DaroLog.Verbose("Native",
                $"Handle[Editor].SetCtaScreenRect adUnit='{_adUnitKey}' rect={rect} touchEnabled={touchEnabled} (mock no-op)");
        }

        public void ClearCtaScreenRect()
        {
            ClearCtaScreenRectCallCount += 1;
            DaroLog.Verbose("Native",
                $"Handle[Editor].ClearCtaScreenRect adUnit='{_adUnitKey}' (mock no-op)");
        }

        public void Dispose()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].Dispose adUnit='{_adUnitKey}'");
            // Coroutine self-cancels at next yield resume via the _disposed gate.
            // No explicit StopCoroutine — keeps cleanup contention-free.
            _disposed = true;
        }

        private static DaroNativeAdInfo BuildMockNativeAdInfo(string adUnitKey)
        {
            // 64×64 magenta — obviously not a real ad; visible in Editor for verification.
            // Texture2D.Destroy in DaroNativeAd.Dispose handles cleanup.
            var icon = new Texture2D(64, 64);
            var pixels = new Color[64 * 64];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.magenta;
            icon.SetPixels(pixels);
            icon.Apply();

            return new DaroNativeAdInfo(
                title:        $"[Mock Ad] {adUnitKey}",
                body:         "This is an Editor mock native ad.",
                callToAction: "Learn More",
                icon:         icon,
                mediaImage:   null,
                assetTypes: new[] { NativeAdAssetType.Title, NativeAdAssetType.Body, NativeAdAssetType.Icon, NativeAdAssetType.CallToAction });   // v1 image-only; video deferred
        }

        internal readonly struct CtaRectCall
        {
            internal CtaRectCall(Rect rect, bool touchEnabled)
            {
                Rect = rect;
                TouchEnabled = touchEnabled;
            }

            internal Rect Rect { get; }
            internal bool TouchEnabled { get; }
        }
    }
}
