#nullable enable
using System.Collections;
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
    /// <c>adUnitId</c> all run independently — no shared state.</para>
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
        private readonly string             _adUnitId;
        private readonly INativeAdEventSink _sink;
        private readonly DaroEditorSettings _settings;
        private bool _disposed;
        private bool _loaded;

        internal DaroEditorNativeAdHandle(
            string adUnitId,
            string? placement,
            INativeAdEventSink sink,
            DaroEditorSettings settings)
        {
            _adUnitId = adUnitId;
            _sink     = sink;
            _settings = settings;
            // placement: ignored in mock (no analytics simulation in v1).
        }

        public void Load(int iconWidth, int iconHeight)
        {
            // iconWidth/iconHeight ignored in mock — Editor doesn't run MAX/Glide
            // sizing logic; mock asset is hard-coded 64×64 magenta. Real
            // dimension flow exercised on device via DaroAndroidNativeAdHandle.
            DaroLog.Verbose("Native", $"Handle[Editor].Load adUnit='{_adUnitId}' icon={iconWidth}x{iconHeight} disposed={_disposed}");
            if (_disposed) return;
            MainThreadDispatcher.EnsureCreated();
            MainThreadDispatcher.RunCoroutine(SimulateLoad());
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
                _loaded = true;

                // -1 sentinel → null latency; positive → millis as-is.
                double? latency = latencyMs < 0 ? (double?)null : latencyMs;
                var adInfo     = new DaroAdInfo(DaroAdFormat.Native, _adUnitId, latency);
                var nativeInfo = BuildMockNativeAdInfo(_adUnitId);

                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_disposed) return;
                    _sink.OnAdLoaded(adInfo, nativeInfo);
                });
            }
            else
            {
                var mapped = DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode);
                var err    = new DaroAdLoadError(mapped, errorMessage, _adUnitId, errorCode);

                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_disposed) return;
                    _sink.OnAdFailedToLoad(err);
                });
            }
        }

        public void NotifyVisible()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].NotifyVisible adUnit='{_adUnitId}' disposed={_disposed} loaded={_loaded}");
            if (_disposed || !_loaded) return;
            // Mock impression: fire only when a load has succeeded; matches
            // device behavior where MAX revenue listener requires a loaded ad.
            var info = new DaroAdInfo(DaroAdFormat.Native, _adUnitId, latency: null);
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_disposed) return;
                _sink.OnAdImpression(info);
            });
        }

        public void NotifyHidden()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].NotifyHidden adUnit='{_adUnitId}' (mock no-op)");
            // Mock: no-op — visibility-hidden doesn't affect impression accounting.
        }

        public void NotifyClicked()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].NotifyClicked adUnit='{_adUnitId}' disposed={_disposed} loaded={_loaded}");
            if (_disposed || !_loaded) return;
            var info = new DaroAdInfo(DaroAdFormat.Native, _adUnitId, latency: null);
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_disposed) return;
                _sink.OnAdClicked(info);
            });
        }

        public void Dispose()
        {
            DaroLog.Verbose("Native", $"Handle[Editor].Dispose adUnit='{_adUnitId}'");
            // Coroutine self-cancels at next yield resume via the _disposed gate.
            // No explicit StopCoroutine — keeps cleanup contention-free.
            _disposed = true;
        }

        private static DaroNativeAdInfo BuildMockNativeAdInfo(string adUnitId)
        {
            // 64×64 magenta — obviously not a real ad; visible in Editor for verification.
            // Texture2D.Destroy in DaroNativeAd.Dispose handles cleanup.
            var icon = new Texture2D(64, 64);
            var pixels = new Color[64 * 64];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.magenta;
            icon.SetPixels(pixels);
            icon.Apply();

            return new DaroNativeAdInfo(
                title:        $"[Mock Ad] {adUnitId}",
                body:         "This is an Editor mock native ad.",
                callToAction: "Learn More",
                icon:         icon,
                mediaImage:   null);   // v1 image-only; video deferred
        }
    }
}
