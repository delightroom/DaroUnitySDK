#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// In-Editor mock implementation of <see cref="IDaroPlatform"/> backed by
    /// <see cref="DaroEditorSettings"/>.
    /// </summary>
    /// <remarks>
    /// <para>Simulates native callback latency via coroutines launched on the hidden
    /// <see cref="MainThreadDispatcher"/> GameObject. Events fire through
    /// <see cref="MainThreadDispatcher.Enqueue(Action)"/> — even though coroutines run
    /// on the main thread — so reentrancy semantics match the native shim flow.</para>
    ///
    /// <para>Error-code surfacing: <see cref="DaroEditorSettings.loadErrorCode"/> and
    /// <c>showErrorCode</c> are raw <see cref="int"/> and pass through
    /// <see cref="DaroAdErrorCodeMapper"/> exactly as native codes would, letting testers
    /// verify the <c>Unspecified</c> fallback end-to-end.</para>
    ///
    /// <para>Dispose-race defense: every coroutine step re-checks the per-unit
    /// <c>_destroyed</c> flag before enqueuing an event closure, and the enqueued closure
    /// re-checks once more at drain time. Destroy cancels any in-flight coroutine for the
    /// unit and drops it from the registry.</para>
    /// </remarks>
    internal sealed class DaroEditorPlatform : IDaroPlatform
    {
        // ── Settings & unit registry ────────────────────────────────────────

        private readonly DaroEditorSettings _settings;
        private readonly Dictionary<string, PerUnitState> _units = new();

        // ── Event slots (set once by DaroSdk) ───────────────────────────────

        private Action<string, DaroAdInfo>?                 _onAdLoaded;
        private Action<string, DaroAdLoadError>?            _onAdFailedToLoad;
        private Action<string, DaroAdInfo>?                 _onAdShown;
        private Action<string, DaroAdDisplayError>?         _onAdFailedToShow;
        private Action<string, DaroAdInfo>?                 _onAdClicked;
        private Action<string, DaroAdInfo>?                 _onAdImpression;
        private Action<string, DaroAdInfo>?                 _onAdDismissed;
        private Action<string, DaroAdInfo, DaroRewardItem>? _onEarnedReward;
        private Action<string, DaroAdInfo>?                 _onAdHidden;
        private Action<string, DaroAdInfo, DaroRevenueInfo>? _onAdRevenuePaid;

        // ── Construction ────────────────────────────────────────────────────

        /// <summary>
        /// Production constructor: resolves <see cref="DaroEditorSettings"/> from
        /// <c>Resources/DaroEditorSettings.asset</c>, falling back to an in-memory
        /// instance with defaults if no asset is present.
        /// </summary>
        internal DaroEditorPlatform()
            : this(LoadOrDefaultSettings())
        {
        }

        /// <summary>
        /// Test-only constructor that skips the <c>Resources.Load</c> lookup.
        /// Allows tests to inject a freshly-tuned <see cref="DaroEditorSettings"/>
        /// instance per scenario.
        /// </summary>
        internal DaroEditorPlatform(DaroEditorSettings settings)
        {
            _settings = settings != null
                ? settings
                : ScriptableObject.CreateInstance<DaroEditorSettings>();
        }

        private static DaroEditorSettings LoadOrDefaultSettings()
        {
            var asset = Resources.Load<DaroEditorSettings>("DaroEditorSettings");
            if (asset != null) return asset;

            DaroLog.Warn("Editor",
                "No DaroEditorSettings asset found under Resources/DaroEditorSettings — " +
                "Editor mock is using built-in defaults. " +
                "Create one via Assets > Create > Daro > Editor Settings to tune mock behavior.");
            return ScriptableObject.CreateInstance<DaroEditorSettings>();
        }

        // ── IDaroPlatform: SDK lifecycle ────────────────────────────────────

        public Task InitializeAsync(DaroSdkInitParams initParams)
        {
            DaroLog.Verbose("Editor", $"Platform[Editor].InitializeAsync logLevel={initParams.LogLevel} delaySec={_settings.initDelaySeconds} shouldSucceed={_settings.initShouldSucceed}");
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Snapshot settings up front so mid-delay Inspector tweaks don't change this run.
            var delay          = _settings.initDelaySeconds;
            var shouldSucceed  = _settings.initShouldSucceed;

            MainThreadDispatcher.EnsureCreated();
            StartCoroutineOnDispatcher(InitCoroutine(delay, shouldSucceed, tcs));
            return tcs.Task;
        }

        private static IEnumerator InitCoroutine(
            float delaySeconds, bool shouldSucceed, TaskCompletionSource<bool> tcs)
        {
            if (delaySeconds > 0f)
                yield return new WaitForSecondsRealtime(delaySeconds);

            if (shouldSucceed)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                tcs.TrySetException(new DaroSdkInitException(
                    "DaroSDK Editor mock: initShouldSucceed=false",
                    (int)DaroAdLoadErrorCode.InitializationFailed));
            }
        }

        // ── IDaroPlatform: Runtime settings (mock no-ops) ───────────────────

        public void SetUserId(string userId)
        {
            DaroLog.Verbose("Editor", $"Platform[Editor].SetUserId userId='{userId}' (mock no-op)");
        }

        internal bool? LastSetAppMuted { get; private set; }
        internal int SetAppMutedCallCount { get; private set; }
        public void SetAppMuted(bool muted)
        {
            DaroLog.Verbose("Editor", $"Platform[Editor].SetAppMuted muted={muted} (mock no-op)");
            LastSetAppMuted = muted;
            SetAppMutedCallCount++;
        }

        // SetLogLevel: no visible side effect (Editor Console is driven by the
        // C# `DaroLog` helper directly via DaroSdk.LogLevel). The mock records
        // the last value + call count so tests can verify the property setter
        // actually propagates to the platform — see DaroSdkLogLevelTests.
        internal DaroLogLevel LastSetLogLevel { get; private set; } = DaroLogLevel.Info;
        internal int          SetLogLevelCallCount { get; private set; }
        public void SetLogLevel(DaroLogLevel level)
        {
            DaroLog.Verbose("Editor", $"Platform[Editor].SetLogLevel level={level} (callCount={SetLogLevelCallCount + 1})");
            LastSetLogLevel       = level;
            SetLogLevelCallCount += 1;
        }

        // DestroyAll: Editor mock has no native side. Mirror the LogLevel
        // observability pattern — record call count so teardown tests can
        // verify the trigger propagation (DaroSdk.MarkShuttingDown →
        // platform.DestroyAll) without exercising real native shims.
        internal int DestroyAllCallCount { get; private set; }
        public void DestroyAll()
        {
            DaroLog.Verbose("Editor", $"Platform[Editor].DestroyAll (callCount={DestroyAllCallCount + 1})");
            DestroyAllCallCount += 1;
        }

        // ── IDaroPlatform: Per-format CRUD (fans into shared helpers) ───────

        public void CreateInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Editor].CreateInterstitial adUnit='{adUnitKey}'");
            CreateUnit(adUnitKey, DaroAdFormat.Interstitial);
        }

        public void CreateRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Editor].CreateRewarded adUnit='{adUnitKey}'");
            CreateUnit(adUnitKey, DaroAdFormat.Rewarded);
        }

        public void CreateAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Editor].CreateAppOpen adUnit='{adUnitKey}'");
            CreateUnit(adUnitKey, DaroAdFormat.AppOpen);
        }

        public void LoadInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Editor].LoadInterstitial adUnit='{adUnitKey}'");
            LoadUnit(adUnitKey);
        }

        public void LoadRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Editor].LoadRewarded adUnit='{adUnitKey}'");
            LoadUnit(adUnitKey);
        }

        public void LoadAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Editor].LoadAppOpen adUnit='{adUnitKey}'");
            LoadUnit(adUnitKey);
        }

        // IsReady* family is polled frequently — no Verbose to avoid spam.
        public bool IsInterstitialReady(string adUnitKey) => IsUnitReady(adUnitKey);
        public bool IsRewardedReady(string adUnitKey)     => IsUnitReady(adUnitKey);
        public bool IsAppOpenReady(string adUnitKey)      => IsUnitReady(adUnitKey);

        public void ShowInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Editor].ShowInterstitial adUnit='{adUnitKey}'");
            ShowUnit(adUnitKey);
        }

        public void ShowRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Editor].ShowRewarded adUnit='{adUnitKey}'");
            ShowUnit(adUnitKey);
        }

        public void ShowAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Editor].ShowAppOpen adUnit='{adUnitKey}'");
            ShowUnit(adUnitKey);
        }

        public void DestroyInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Editor].DestroyInterstitial adUnit='{adUnitKey}'");
            DestroyUnit(adUnitKey);
        }

        public void DestroyRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Editor].DestroyRewarded adUnit='{adUnitKey}'");
            DestroyUnit(adUnitKey);
        }

        public void DestroyAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Editor].DestroyAppOpen adUnit='{adUnitKey}'");
            DestroyUnit(adUnitKey);
        }

        public void SetRewardedCustomData(string adUnitKey, string customData)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Editor].SetRewardedCustomData adUnit='{adUnitKey}' len={customData.Length} (mock no-op)");
            /* no-op in Editor mock — consumer set was acknowledged */
        }

        // ── IDaroPlatform: event slots ──────────────────────────────────────

        public Action<string, DaroAdInfo>?                 OnAdLoaded       { set => _onAdLoaded       = value; }
        public Action<string, DaroAdLoadError>?            OnAdFailedToLoad { set => _onAdFailedToLoad = value; }
        public Action<string, DaroAdInfo>?                 OnAdShown        { set => _onAdShown        = value; }
        public Action<string, DaroAdDisplayError>?         OnAdFailedToShow { set => _onAdFailedToShow = value; }
        public Action<string, DaroAdInfo>?                 OnAdClicked      { set => _onAdClicked      = value; }
        public Action<string, DaroAdInfo>?                 OnAdImpression   { set => _onAdImpression   = value; }
        public Action<string, DaroAdInfo>?                 OnAdDismissed    { set => _onAdDismissed    = value; }
        public Action<string, DaroAdInfo, DaroRewardItem>? OnEarnedReward   { set => _onEarnedReward   = value; }
        public Action<string, DaroAdInfo>?                 OnAdHidden       { set => _onAdHidden       = value; }
        public Action<string, DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid { set => _onAdRevenuePaid  = value; }

        /// <summary>
        /// Mock revenue payload from settings — same micros→decimal conversion
        /// the Android wire uses, so Editor verifies the real mapping path.
        /// </summary>
        private DaroRevenueInfo BuildMockRevenue() =>
            DaroRevenueInfo.FromMicros(
                _settings.revenueValueMicros,
                _settings.revenueCurrencyCode ?? "USD",
                _settings.revenuePrecisionType);

        // ── Banner mock impl ──────────────────────────────────
        // LoadBanner reuses LoadUnit's coroutine — same deterministic /
        // always-fail policy as fullscreen formats. On successful banner load,
        // the mock overlay is visible by default. ShowBanner / HideBanner are
        // not coroutine-driven (banner is a persistent overlay, not one-shot)
        // — they synchronously update state then enqueue events.

#if UNITY_EDITOR
        // Per-banner GameObject hosting DaroEditorBannerView for IMGUI overlay.
        // Editor-only — never instantiated in player builds (DaroEditorPlatform
        // itself only exists under #if UNITY_EDITOR via DaroPlatform.Current).
        private readonly Dictionary<string, GameObject> _bannerViews = new();
#endif

        public void CreateBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[Editor].CreateBanner adUnit='{adUnitKey}'");
            CreateUnit(adUnitKey, DaroAdFormat.Banner);
        }

        public void LoadBanner(string adUnitKey, DaroBannerSize size)
        {
            DaroLog.Verbose("Banner", $"Platform[Editor].LoadBanner adUnit='{adUnitKey}' size={size} unit={_units.ContainsKey(adUnitKey)}");
            if (_units.TryGetValue(adUnitKey, out var state))
            {
                state.BannerSize = size;
                state.Visible = true;
            }
            LoadUnit(adUnitKey);
        }

        public void ShowBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[Editor].ShowBanner adUnit='{adUnitKey}' unit={_units.ContainsKey(adUnitKey)}");
            if (!_units.TryGetValue(adUnitKey, out var state)) return;
            if (state.Destroyed || !state.Loaded) return;
            if (state.Visible) return;
            state.Visible = true;

#if UNITY_EDITOR
            EnsureBannerView(state).Show();
#endif

            // OnAdShown is synthesized by DaroBannerAd. We fire
            // OnAdImpression here as the platform-side signal that the overlay
            // is live. Revenue follows the impression — mirrors the device
            // contract where didPayRevenue is the impression signal.
            var info = new DaroAdInfo(DaroAdFormat.Banner, adUnitKey, latency: null);
            var revenue = BuildMockRevenue();
            var captureId = adUnitKey;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (state.Destroyed || !state.Visible) return;
                _onAdImpression?.Invoke(captureId, info);
                _onAdRevenuePaid?.Invoke(captureId, info, revenue);
            });
        }

        public void HideBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[Editor].HideBanner adUnit='{adUnitKey}' unit={_units.ContainsKey(adUnitKey)}");
            if (!_units.TryGetValue(adUnitKey, out var state)) return;
            if (state.Destroyed) return;
            var wasDisplayed = state.BannerDisplayed;
            state.Visible = false;
            state.BannerDisplayed = false;

#if UNITY_EDITOR
            if (_bannerViews.TryGetValue(adUnitKey, out var go) && go != null)
            {
                go.GetComponent<DaroEditorBannerView>()?.Hide();
            }
#endif

            if (!wasDisplayed) return;

            var info = new DaroAdInfo(DaroAdFormat.Banner, adUnitKey, latency: null);
            var captureId = adUnitKey;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (state.Destroyed) return;
                _onAdHidden?.Invoke(captureId, info);
            });
        }

        public void DestroyBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[Editor].DestroyBanner adUnit='{adUnitKey}'");
#if UNITY_EDITOR
            if (_bannerViews.TryGetValue(adUnitKey, out var go) && go != null)
            {
                UnityEngine.Object.Destroy(go);
                _bannerViews.Remove(adUnitKey);
            }
#endif
            DestroyUnit(adUnitKey);
        }

        public void SetBannerPosition(string adUnitKey, DaroBannerPosition position)
        {
            DaroLog.Verbose("Banner", $"Platform[Editor].SetBannerPosition adUnit='{adUnitKey}' position={position} unit={_units.ContainsKey(adUnitKey)}");
            if (!_units.TryGetValue(adUnitKey, out var state)) return;
            if (state.Destroyed) return;
            state.BannerPosition = position;

#if UNITY_EDITOR
            if (_bannerViews.TryGetValue(adUnitKey, out var go) && go != null)
            {
                go.GetComponent<DaroEditorBannerView>()?.SetPosition(position);
            }
#endif
        }

        // Non-authoritative mock footprint — only while the mock banner is
        // visible (Load/Show display, not Hidden). Device platforms return the real
        // measured rect; the editor returns a nominal-size rect from Screen.safeArea.
        public bool TryGetBannerScreenRect(string adUnitKey, out Rect rect)
        {
            rect = default;
#if UNITY_EDITOR
            if (_bannerViews.TryGetValue(adUnitKey, out var go) && go != null)
            {
                var view = go.GetComponent<DaroEditorBannerView>();
                if (view != null && view.IsVisible)
                {
                    rect = view.ScreenRectBottomLeft();
                    return true;
                }
            }
#endif
            return false;
        }

#if UNITY_EDITOR
        private DaroEditorBannerView EnsureBannerView(PerUnitState state)
        {
            if (_bannerViews.TryGetValue(state.AdUnitKey, out var existing) && existing != null)
            {
                return existing.GetComponent<DaroEditorBannerView>();
            }

            var go = new GameObject($"DaroEditorBannerView[{state.AdUnitKey}]")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityEngine.Object.DontDestroyOnLoad(go);
            var view = go.AddComponent<DaroEditorBannerView>();
            view.Configure(state.AdUnitKey, state.BannerSize, state.BannerPosition);
            _bannerViews[state.AdUnitKey] = go;
            return view;
        }
#endif

        // ── Light Popup mock impl ───────────────────────────────────────────
        // Pure coroutine simulation — no UI. Lifecycle accuracy over visual
        // approximation: Light Popup is fullscreen modal on device, Editor mock
        // just exercises Load/Show/Dismiss timing via _settings.adDurationSeconds.
        // Color options silently ignored (visual fidelity not
        // worth IMGUI cost when format is fullscreen modal).

        public void CreateLightPopup(string adUnitKey, DaroLightPopupAdOptions? options)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Editor].CreateLightPopup adUnit='{adUnitKey}'");
            CreateUnit(adUnitKey, DaroAdFormat.LightPopup);
        }

        public void LoadLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Editor].LoadLightPopup adUnit='{adUnitKey}'");
            LoadUnit(adUnitKey);
        }

        public bool IsLightPopupReady(string adUnitKey) => IsUnitReady(adUnitKey);

        public void ShowLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Editor].ShowLightPopup adUnit='{adUnitKey}'");
            ShowUnit(adUnitKey);
        }

        public void DestroyLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Editor].DestroyLightPopup adUnit='{adUnitKey}'");
            DestroyUnit(adUnitKey);
        }

        // ── Unit state ──────────────────────────────────────────────────────

        /// <summary>
        /// Per-ad-unit mock state. <c>_destroyed</c> is the dispose-race guard;
        /// every coroutine step checks it before enqueuing an event.
        /// </summary>
        private sealed class PerUnitState
        {
            public readonly string      AdUnitKey;
            public readonly DaroAdFormat Format;
            public bool                 Loaded;
            public bool                 Showing;
            public volatile bool        Destroyed;

            // Banner-only fields — ignored for fullscreen formats.
            public bool                 Visible;
            public bool                 BannerDisplayed;
            public int                  LoadGeneration;
            public DaroBannerSize       BannerSize;
            public DaroBannerPosition   BannerPosition;

            public PerUnitState(string adUnitKey, DaroAdFormat format)
            {
                AdUnitKey  = adUnitKey;
                Format    = format;
            }
        }

        private void CreateUnit(string adUnitKey, DaroAdFormat format)
        {
            // Duplicate-construction-replaces rule: destroy existing first.
            if (_units.TryGetValue(adUnitKey, out var existing))
            {
                existing.Destroyed = true;
                _units.Remove(adUnitKey);
            }

            _units[adUnitKey] = new PerUnitState(adUnitKey, format);
        }

        private void LoadUnit(string adUnitKey)
        {
            if (!_units.TryGetValue(adUnitKey, out var state)) return;
            if (state.Destroyed) return;
            state.Loaded = false;

            MainThreadDispatcher.EnsureCreated();
            var generation = 0;
            if (state.Format == DaroAdFormat.Banner)
            {
                generation = ++state.LoadGeneration;
            }
            StartCoroutineOnDispatcher(LoadCoroutine(state, generation));
        }

        private static bool IsStaleBannerLoad(PerUnitState state, int generation)
        {
            return state.Format == DaroAdFormat.Banner
                && state.LoadGeneration != generation;
        }

        private IEnumerator LoadCoroutine(PerUnitState state, int generation)
        {
            // Snapshot settings so mid-delay changes don't desync one load cycle.
            var delay        = _settings.loadDelaySeconds;
            var successRate  = _settings.loadSuccessRate;
            var latencyMs    = _settings.loadLatencyMs;
            var errorCode    = _settings.loadErrorCode;
            var errorMessage = _settings.loadErrorMessage ?? string.Empty;

            if (delay > 0f)
                yield return new WaitForSecondsRealtime(delay);

            if (state.Destroyed || IsStaleBannerLoad(state, generation)) yield break;

            var success = DaroEditorMockProbability.RollSuccess(successRate);
            if (success)
            {
                state.Loaded = true;

                // latency semantics: -1 → null, positive → millis as-is
                // (matching Daro cross-platform contract).
                double? latency = latencyMs < 0 ? (double?)null : latencyMs;
                var info = new DaroAdInfo(state.Format, state.AdUnitKey, latency);

                if (state.Format == DaroAdFormat.Banner && state.Visible)
                {
#if UNITY_EDITOR
                    EnsureBannerView(state).Show();
#endif
                    state.BannerDisplayed = true;
                }

                var adUnitKey = state.AdUnitKey;
                var revenue = BuildMockRevenue();
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (state.Destroyed) return;
                    _onAdLoaded?.Invoke(adUnitKey, info);
                    if (state.Destroyed || IsStaleBannerLoad(state, generation)) return;
                    if (state.Format == DaroAdFormat.Banner && state.Visible && state.Loaded)
                    {
                        _onAdImpression?.Invoke(adUnitKey, info);
                        _onAdRevenuePaid?.Invoke(adUnitKey, info, revenue);
                    }
                });
            }
            else
            {
                state.Loaded = false;
                if (state.Format == DaroAdFormat.Banner)
                {
                    state.Visible = false;
                    state.BannerDisplayed = false;
#if UNITY_EDITOR
                    if (_bannerViews.TryGetValue(state.AdUnitKey, out var go) && go != null)
                    {
                        go.GetComponent<DaroEditorBannerView>()?.Hide();
                    }
#endif
                }

                var mapped = DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode);
                var err = new DaroAdLoadError(mapped, errorMessage, state.AdUnitKey);

                var adUnitKey = state.AdUnitKey;
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (state.Destroyed || IsStaleBannerLoad(state, generation)) return;
                    _onAdFailedToLoad?.Invoke(adUnitKey, err);
                });
            }
        }

        private bool IsUnitReady(string adUnitKey)
        {
            return _units.TryGetValue(adUnitKey, out var state)
                && !state.Destroyed
                && state.Loaded
                && !state.Showing;
        }

        private void ShowUnit(string adUnitKey)
        {
            if (!_units.TryGetValue(adUnitKey, out var state)) return;
            if (state.Destroyed) return;

            MainThreadDispatcher.EnsureCreated();
            StartCoroutineOnDispatcher(ShowCoroutine(state));
        }

        private IEnumerator ShowCoroutine(PerUnitState state)
        {
            var showDelay         = _settings.showDelaySeconds;
            var showSuccessRate   = _settings.showSuccessRate;
            var showErrorCode     = _settings.showErrorCode;
            var showErrorMessage  = _settings.showErrorMessage ?? string.Empty;
            var adDurationSeconds = _settings.adDurationSeconds;
            var rewardAmount      = _settings.rewardAmount;
            var rewardType        = _settings.rewardType ?? string.Empty;

            // Case: Show called before Load completed → OnAdFailedToShow.
            if (!state.Loaded)
            {
                var notReady = DaroAdErrorCodeMapper.ToDisplayErrorCode(
                    (int)DaroAdDisplayErrorCode.FullscreenAdNotReady);
                var notReadyErr = new DaroAdDisplayError(notReady, "Ad not ready (Editor mock)");
                var adUnitKeyNR = state.AdUnitKey;
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (state.Destroyed) return;
                    _onAdFailedToShow?.Invoke(adUnitKeyNR, notReadyErr);
                });
                yield break;
            }

            state.Showing = true;

            if (showDelay > 0f)
                yield return new WaitForSecondsRealtime(showDelay);

            if (state.Destroyed) yield break;

            var success = DaroEditorMockProbability.RollSuccess(showSuccessRate);
            if (!success)
            {
                var mapped = DaroAdErrorCodeMapper.ToDisplayErrorCode(showErrorCode);
                var err = new DaroAdDisplayError(mapped, showErrorMessage);
                var adUnitKeyF = state.AdUnitKey;
                state.Showing = false;
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (state.Destroyed) return;
                    _onAdFailedToShow?.Invoke(adUnitKeyF, err);
                });
                yield break;
            }

            // Success: fire OnAdShown, then wait adDurationSeconds, then OnAdDismissed
            // (and for Rewarded, OnEarnedReward just before OnAdDismissed).
            var shownInfo = BuildShownInfo(state);
            var adUnitKey = state.AdUnitKey;
            var revenue  = BuildMockRevenue();
            MainThreadDispatcher.Enqueue(() =>
            {
                if (state.Destroyed) return;
                _onAdShown?.Invoke(adUnitKey, shownInfo);
                // ILRD: device contract reports revenue at impression time
                // (didPayRevenue ≈ show for fullscreen formats).
                _onAdRevenuePaid?.Invoke(adUnitKey, shownInfo, revenue);
            });

            if (adDurationSeconds > 0f)
                yield return new WaitForSecondsRealtime(adDurationSeconds);

            if (state.Destroyed) yield break;

            if (state.Format == DaroAdFormat.Rewarded)
            {
                var rewardInfo = BuildShownInfo(state);
                var rewardItem = new DaroRewardItem(rewardAmount, rewardType);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (state.Destroyed) return;
                    _onEarnedReward?.Invoke(adUnitKey, rewardInfo, rewardItem);
                });
            }

            var dismissInfo = BuildShownInfo(state);
            // Ad has been consumed — ready-state is false after dismiss; consumer must reload.
            state.Loaded = false;
            state.Showing = false;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (state.Destroyed) return;
                _onAdDismissed?.Invoke(adUnitKey, dismissInfo);
            });
        }

        private static DaroAdInfo BuildShownInfo(PerUnitState state)
        {
            // Latency is a load-phase metric. Post-load AdInfo surfaces null
            // to match native behavior, which does not re-report latency
            // on show/dismiss.
            return new DaroAdInfo(state.Format, state.AdUnitKey, latency: null);
        }

        private void DestroyUnit(string adUnitKey)
        {
            if (!_units.TryGetValue(adUnitKey, out var state)) return;
            state.Destroyed = true;
            _units.Remove(adUnitKey);
            // No explicit coroutine cancellation call: the Destroyed flag gates every
            // yield-resume point (see LoadCoroutine / ShowCoroutine) and the enqueued
            // event closures, so any in-flight coroutine self-terminates on next step.
        }

        // ── Coroutine host helper ───────────────────────────────────────────

        // Delegates to MainThreadDispatcher's RunCoroutine static helper; noops
        // if EnsureCreated hasn't run. Callers (Initialize / Load / Show flows)
        // must have called MainThreadDispatcher.EnsureCreated() before reaching
        // here — DaroSdk.InitializeAsync handles that at the top of its path.
        // (Named RunCoroutine, not StartCoroutine, to avoid CS0176 collision
        // with MonoBehaviour.StartCoroutine.)
        private static void StartCoroutineOnDispatcher(IEnumerator coroutine)
        {
            MainThreadDispatcher.RunCoroutine(coroutine);
        }

        // ── Native ad (instance-owned) ──────────────────────────────
        // Each DaroNativeAd gets its own DaroEditorNativeAdHandle (per-instance
        // coroutine + per-instance mock asset). Multi-instance: N handles with
        // same adUnitKey run independently.
        internal DaroEditorNativeAdHandle? LastNativeAdHandle { get; private set; }

        public INativeAdHandle CreateNativeAdHandle(string adUnitKey, INativeAdEventSink sink)
        {
            DaroLog.Verbose("Native", $"Platform[Editor].CreateNativeAdHandle adUnit='{adUnitKey}'");
            var handle = new DaroEditorNativeAdHandle(adUnitKey, sink, _settings);
            LastNativeAdHandle = handle;
            return handle;
        }
    }
}
