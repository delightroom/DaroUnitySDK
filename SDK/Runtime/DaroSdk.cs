#nullable enable

using System;
using System.Threading.Tasks;
using Daro.Internal;

namespace Daro
{
    /// <summary>
    /// Static facade for the Daro Unity SDK. See native-bridge-architecture.md §2.3.
    /// Privacy / log settings should be configured before <see cref="InitializeAsync"/>.
    /// </summary>
    public static class DaroSdk
    {
        // ── Init state ───────────────────────────────────────────────────────

        /// <summary>
        /// True once <see cref="InitializeAsync"/> has completed successfully.
        /// </summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// TaskCompletionSource that backs the idempotent
        /// <see cref="InitializeAsync"/> contract. Null before the first call,
        /// non-null (in-flight or completed) afterwards. Reset to null by
        /// <see cref="ResetStatics"/> so a second play session starts clean.
        /// </summary>
        private static TaskCompletionSource<bool>? _initTcs;

        /// <summary>
        /// Backing delegate for <see cref="OnSdkInitialized"/>. Custom add/remove
        /// accessors on the event implement the "late subscriber fires immediately"
        /// contract per §2.3, which the stock <c>event</c> keyword can't express.
        /// </summary>
        private static Action? _onSdkInitialized;

        /// <summary>
        /// Fires once on successful SDK initialization.
        /// <para>
        /// <b>Late-subscriber contract</b> (§2.3): if the SDK is already
        /// initialized at the time a handler subscribes, the new handler is
        /// invoked synchronously on the subscribing thread — zero frame delay,
        /// no queuing. Consumers writing <c>Start()</c> methods that subscribe
        /// after an <c>await DaroSdk.InitializeAsync()</c> still observe the event.
        /// </para>
        /// </summary>
        public static event Action OnSdkInitialized
        {
            add
            {
                _onSdkInitialized += value;

                // Late-subscriber: if init already completed, fire immediately
                // so `await InitializeAsync(); DaroSdk.OnSdkInitialized += h;`
                // still triggers h. Branch on main-thread to preserve the
                // "all SDK callbacks land on the main thread" invariant: a
                // main-thread subscriber gets the original zero-frame-delay
                // sync invoke, a worker-thread subscriber is marshalled via
                // the dispatcher (deferred to next Update tick on main thread).
                if (IsInitialized)
                {
                    if (MainThreadDispatcher.IsMainThread())
                    {
                        // Subscriber's own thread — let throws propagate back
                        // to the +=  caller. They're observing their own bug
                        // synchronously, which is the right debugging signal.
                        value.Invoke();
                    }
                    else
                    {
                        // Worker-thread late subscriber: deferred to main via
                        // dispatcher. Wrap with SafeEventInvoker so a throw
                        // here doesn't kill the main-thread drain loop —
                        // the throw happens on a different thread than the
                        // subscriber call, so the asymmetric main-thread blow-
                        // up would be unexpected from the publisher's POV.
                        MainThreadDispatcher.Enqueue(() => SafeEventInvoker.Invoke(value));
                    }
                }
            }
            remove
            {
                _onSdkInitialized -= value;
            }
        }

        // ── Privacy settings (set before Initialize) ─────────────────────────

        public static bool?   HasGdprConsent                    { get; set; }
        public static string? GdprConsentString                 { get; set; }
        public static bool?   DoNotSell                         { get; set; }
        public static string? CcpaConsentString                 { get; set; }
        public static bool?   IsTaggedForChildDirectedTreatment { get; set; }

        // ── Logging ──────────────────────────────────────────────────────────

        // Volatile backing field — DaroLogLevel's underlying type is int, so
        // volatile read/write is atomic on every supported runtime. The
        // finalizer-safe inline gate at consumer call sites reads this value
        // directly (no lock — locks deadlock in finalizers). See sketch §A4.
        private static volatile DaroLogLevel _logLevel = DaroLogLevel.Info;

        /// <summary>
        /// Verbosity for SDK-emitted logs. Setter immediately propagates the
        /// new level to the platform shim (init-time + runtime, see sketch §A4)
        /// so the C# Console gate and the Android Kotlin shim's
        /// <c>daroLogLevel</c> stay in lockstep. Pre-init assignment is safe —
        /// <see cref="DaroPlatform.Current"/> lazily provides the Editor stub.
        /// </summary>
        public static DaroLogLevel LogLevel
        {
            get => _logLevel;
            set
            {
                _logLevel = value;
                DaroLog.Verbose("Sdk", $"LogLevel set to {value} — propagating to platform");
                DaroPlatform.Current.SetLogLevel(value);
            }
        }

        // ── Initialization ───────────────────────────────────────────────────

        /// <summary>
        /// Initialize the Daro SDK. Idempotent (§2.3): duplicate calls return
        /// the same <see cref="Task"/> — an in-flight one while init is running,
        /// a completed one once init has finished.
        /// </summary>
        /// <remarks>
        /// Must be called on the Unity main thread; internally constructs the
        /// hidden <c>MainThreadDispatcher</c> GameObject. Calling off the main
        /// thread throws <c>UnityException</c> from <c>new GameObject(...)</c>
        /// per §6.3 — not SDK-enforced, a documented consumer contract.
        /// </remarks>
        /// <exception cref="DaroSdkInitException">
        /// Surfaced via a faulted Task when the native init fails.
        /// </exception>
        public static Task InitializeAsync()
        {
            // Idempotency per §2.3: any second call — in-flight or completed —
            // returns the same Task. We latch the TCS on the first call;
            // subsequent callers simply observe its state.
            if (_initTcs != null)
            {
                DaroLog.Verbose("Sdk",
                    $"InitializeAsync idempotent return (status={_initTcs.Task.Status})");
                return _initTcs.Task;
            }

            DaroLog.Verbose("Sdk", "InitializeAsync first call — starting platform init");

            // Create and publish the TCS before any platform call so a
            // concurrent second caller sees the in-flight state. This isn't
            // strictly thread-safe — two simultaneous first-callers could both
            // race past the null check — but InitializeAsync is a
            // main-thread-only API (see §6.3) so there's only one caller here
            // in practice. RunContinuationsAsynchronously avoids consumer
            // continuations running inline inside this method.
            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _initTcs = tcs;

            // EnsureCreated is the main-thread-only call. If we're off the main
            // thread the UnityException will propagate out of InitializeAsync
            // synchronously — documented behavior per §4.1 / §6.3. We deliberately
            // don't catch it: a consumer calling off the main thread should see
            // a synchronous throw, not a faulted Task (the latter would be
            // indistinguishable from a native init failure).
            MainThreadDispatcher.EnsureCreated();

            // Wire platform → consumer event plumbing (§2.4). The IDaroPlatform
            // event setters are set-once by contract, so doing it here — inside
            // the first InitializeAsync call — is exactly the right moment. The
            // closures route by (format, adUnitId) → instance via the registry;
            // each firing method on the instance re-checks `_disposed` at drain
            // time per §4.4.
            WirePlatformEvents();

            var initParams = new DaroSdkInitParams
            {
                HasGdprConsent                    = HasGdprConsent,
                GdprConsentString                 = GdprConsentString,
                DoNotSell                         = DoNotSell,
                CcpaConsentString                 = CcpaConsentString,
                IsTaggedForChildDirectedTreatment = IsTaggedForChildDirectedTreatment,
                LogLevel                          = LogLevel,
            };

            // Fire the platform init and arrange completion handling via
            // ContinueWith rather than async/await so this method stays
            // strictly synchronous on the calling thread — the Task it returns
            // is the TCS Task, not a compiler-generated state machine.
            //
            // The continuation marshals completion onto the main thread via
            // MainThreadDispatcher so OnSdkInitialized fires on the main
            // thread (consistent with all other Daro events per §3.3). The
            // Task itself completes on whatever thread the dispatcher drains
            // on — again, the main thread.
            Task platformInit;
            try
            {
                platformInit = DaroPlatform.Current.InitializeAsync(initParams);
            }
            catch (Exception e)
            {
                // Synchronous throw from the platform layer (or the
                // resolver itself) — surface via the TCS so every caller
                // observes the same faulted Task. Without this the first
                // caller would see a sync exception and subsequent ones
                // would wait forever on a never-completed TCS.
                tcs.TrySetException(e);
                return tcs.Task;
            }

            platformInit.ContinueWith(t =>
            {
                // Marshal onto the main thread so event handlers and Task
                // continuations land in the same threading environment as
                // every other SDK callback.
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (t.IsFaulted)
                    {
                        // Flatten AggregateException down to its first inner;
                        // consumers awaiting InitializeAsync should see the
                        // native DaroSdkInitException directly, not wrapped.
                        var ex = t.Exception?.InnerException ?? t.Exception!;
                        tcs.TrySetException(ex);
                        return;
                    }

                    if (t.IsCanceled)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    // Success path: flip IsInitialized BEFORE firing the event,
                    // so handlers that check IsInitialized see the post-init
                    // state, and a late subscriber adding inside a handler
                    // also observes true.
                    IsInitialized = true;
                    DaroLog.Verbose("Sdk",
                        "InitializeAsync success — IsInitialized=true, firing OnSdkInitialized");
                    SafeEventInvoker.Invoke(_onSdkInitialized);
                    tcs.TrySetResult(true);
                });
            }, TaskScheduler.Default);

            return tcs.Task;
        }

        // ── Runtime settings ─────────────────────────────────────────────────

        /// <summary>
        /// Set an opaque user id on DaroSDK. Safe to call pre-init (§2.3):
        /// platform stores-and-forwards as needed.
        /// </summary>
        public static void SetUserId(string userId)
        {
            if (userId == null) throw new ArgumentNullException(nameof(userId));
            DaroLog.Verbose("Sdk", $"SetUserId userId='{userId}'");
            DaroPlatform.Current.SetUserId(userId);
        }

        /// <summary>
        /// Mute or unmute all Daro ad audio. Safe to call pre-init (§2.3):
        /// platform stores-and-forwards as needed.
        /// </summary>
        public static void SetAppMuted(bool muted)
        {
            DaroLog.Verbose("Sdk", $"SetAppMuted muted={muted}");
            DaroPlatform.Current.SetAppMuted(muted);
        }

        // ── Internal ─────────────────────────────────────────────────────────

        /// <summary>
        /// Reset all static state. Called by <c>DaroRuntimeBoot.Reset</c> on
        /// play-mode enter / build startup (§6.4). After this runs, the SDK
        /// behaves as if no prior session had existed.
        /// </summary>
        internal static void ResetStatics()
        {
            IsInitialized                     = false;
            _initTcs                          = null;
            _onSdkInitialized                 = null;
            HasGdprConsent                    = null;
            GdprConsentString                 = null;
            DoNotSell                         = null;
            CcpaConsentString                 = null;
            IsTaggedForChildDirectedTreatment = null;
            // Direct field write — bypass the property setter so ResetStatics
            // stays side-effect-free (no SetLogLevel propagate during teardown).
            // Original behavior was an auto-property reset; preserved.
            _logLevel                         = DaroLogLevel.Info;
        }

        /// <summary>
        /// Install the eight platform-level event setters. Each handler
        /// resolves the consumer-facing ad instance via the registry, does
        /// the §4.4 pre-enqueue disposed check, and delegates to the
        /// instance's internal <c>Fire*</c> method.
        /// </summary>
        /// <remarks>
        /// Called exactly once per process, from the first
        /// <see cref="InitializeAsync"/>. The <c>IDaroPlatform</c> setters
        /// are set-once by contract (§2.5), so re-entering this method
        /// would be incorrect — but idempotency in <see cref="InitializeAsync"/>
        /// already prevents that.
        /// </remarks>
        private static void WirePlatformEvents()
        {
            var platform = DaroPlatform.Current;

            platform.OnAdLoaded = (adUnitId, info) =>
            {
                RoutePentaInfo(adUnitId, info,
                    i  => i.FireOnAdLoaded(info),
                    r  => r.FireOnAdLoaded(info),
                    a  => a.FireOnAdLoaded(info),
                    b  => b.FireOnAdLoaded(info),
                    lp => lp.FireOnAdLoaded(info));
            };

            platform.OnAdFailedToLoad = (adUnitId, error) =>
            {
                RoutePentaLoadError(adUnitId, error);
            };

            platform.OnAdShown = (adUnitId, info) =>
            {
                RouteFullscreenInfo(adUnitId, info,
                    i  => i.FireOnAdShown(info),
                    r  => r.FireOnAdShown(info),
                    a  => a.FireOnAdShown(info),
                    lp => lp.FireOnAdShown(info));
            };

            platform.OnAdFailedToShow = (adUnitId, error) =>
            {
                RouteFullscreenDisplayError(adUnitId, error);
            };

            platform.OnAdClicked = (adUnitId, info) =>
            {
                RoutePentaInfo(adUnitId, info,
                    i  => i.FireOnAdClicked(info),
                    r  => r.FireOnAdClicked(info),
                    a  => a.FireOnAdClicked(info),
                    b  => b.FireOnAdClicked(info),
                    lp => lp.FireOnAdClicked(info));
            };

            platform.OnAdImpression = (adUnitId, info) =>
            {
                RoutePentaInfo(adUnitId, info,
                    i  => i.FireOnAdImpression(info),
                    r  => r.FireOnAdImpression(info),
                    a  => a.FireOnAdImpression(info),
                    b  => b.FireOnAdImpression(info),
                    lp => lp.FireOnAdImpression(info));
            };

            platform.OnAdDismissed = (adUnitId, info) =>
            {
                RouteFullscreenInfo(adUnitId, info,
                    i  => i.FireOnAdDismissed(info),
                    r  => r.FireOnAdDismissed(info),
                    a  => a.FireOnAdDismissed(info),
                    lp => lp.FireOnAdDismissed(info));
            };

            platform.OnEarnedReward = (adUnitId, info, reward) =>
            {
                // OnEarnedReward is rewarded-only; no interstitial/appopen branch.
                var rewarded = DaroAdInstanceRegistry.Find<DaroRewardedAd>(
                    DaroAdFormat.Rewarded, adUnitId);
                if (rewarded == null)
                {
                    DaroLog.Verbose("Sdk", $"OnEarnedReward adUnit='{adUnitId}' → no Rewarded instance (registry miss)");
                    return;
                }
                DaroLog.Verbose("Sdk", $"OnEarnedReward adUnit='{adUnitId}' reward={reward.Amount} '{reward.RewardType}' → Rewarded");
                rewarded.FireOnEarnedReward(info, reward);
            };

            platform.OnAdHidden = (adUnitId, info) =>
            {
                // OnAdHidden is banner-only; fired from platform impl after Hide
                // completes (no native callback exists for banner hide).
                var banner = DaroAdInstanceRegistry.Find<DaroBannerAd>(
                    DaroAdFormat.Banner, adUnitId);
                DaroLog.Verbose("Sdk", $"OnAdHidden adUnit='{adUnitId}' → Banner (match={banner != null})");
                banner?.FireOnAdHidden(info);
            };

            DaroLog.Verbose("Sdk", "WirePlatformEvents complete — 9 event slots installed");
        }

        // ── Routing helpers ──────────────────────────────────────────────────
        //
        // The platform layer emits adUnitId-keyed callbacks without a format
        // discriminator. Since adUnitId is, in principle, not unique across
        // formats, we try each format's registry in turn. In practice a
        // given adUnitId belongs to exactly one format — the registry lookup
        // returns null for the others and each returns-early cleanly.
        //
        // Penta = all 5 formats (Interstitial / Rewarded / AppOpen / Banner /
        // LightPopup) — events that every format participates in (Loaded /
        // FailedToLoad / Clicked / Impression).
        //
        // Fullscreen = the 4 fullscreen formats only (Banner excluded) — events
        // banner has no native callback for (Shown / FailedToShow / Dismissed).
        // Banner Show fires sync from the facade; Banner has no Dismissed concept.

        private static void RoutePentaInfo(
            string adUnitId,
            DaroAdInfo info,
            Action<DaroInterstitialAd> onInterstitial,
            Action<DaroRewardedAd>     onRewarded,
            Action<DaroAppOpenAd>      onAppOpen,
            Action<DaroBannerAd>       onBanner,
            Action<DaroLightPopupAd>   onLightPopup)
        {
            var i = DaroAdInstanceRegistry.Find<DaroInterstitialAd>(
                DaroAdFormat.Interstitial, adUnitId);
            if (i != null) { DaroLog.Verbose("Sdk", $"RoutePentaInfo adUnit='{adUnitId}' → Interstitial"); onInterstitial(i); return; }

            var r = DaroAdInstanceRegistry.Find<DaroRewardedAd>(
                DaroAdFormat.Rewarded, adUnitId);
            if (r != null) { DaroLog.Verbose("Sdk", $"RoutePentaInfo adUnit='{adUnitId}' → Rewarded"); onRewarded(r); return; }

            var a = DaroAdInstanceRegistry.Find<DaroAppOpenAd>(
                DaroAdFormat.AppOpen, adUnitId);
            if (a != null) { DaroLog.Verbose("Sdk", $"RoutePentaInfo adUnit='{adUnitId}' → AppOpen"); onAppOpen(a); return; }

            var b = DaroAdInstanceRegistry.Find<DaroBannerAd>(
                DaroAdFormat.Banner, adUnitId);
            if (b != null) { DaroLog.Verbose("Sdk", $"RoutePentaInfo adUnit='{adUnitId}' → Banner"); onBanner(b); return; }

            var lp = DaroAdInstanceRegistry.Find<DaroLightPopupAd>(
                DaroAdFormat.LightPopup, adUnitId);
            if (lp != null) { DaroLog.Verbose("Sdk", $"RoutePentaInfo adUnit='{adUnitId}' → LightPopup"); onLightPopup(lp); return; }

            DaroLog.Verbose("Sdk", $"RoutePentaInfo adUnit='{adUnitId}' → no match (registry miss)");
        }

        private static void RoutePentaLoadError(string adUnitId, DaroAdLoadError error)
        {
            var i = DaroAdInstanceRegistry.Find<DaroInterstitialAd>(
                DaroAdFormat.Interstitial, adUnitId);
            if (i != null) { DaroLog.Verbose("Sdk", $"RoutePentaLoadError adUnit='{adUnitId}' code={error.Code} → Interstitial"); i.FireOnAdFailedToLoad(error); return; }

            var r = DaroAdInstanceRegistry.Find<DaroRewardedAd>(
                DaroAdFormat.Rewarded, adUnitId);
            if (r != null) { DaroLog.Verbose("Sdk", $"RoutePentaLoadError adUnit='{adUnitId}' code={error.Code} → Rewarded"); r.FireOnAdFailedToLoad(error); return; }

            var a = DaroAdInstanceRegistry.Find<DaroAppOpenAd>(
                DaroAdFormat.AppOpen, adUnitId);
            if (a != null) { DaroLog.Verbose("Sdk", $"RoutePentaLoadError adUnit='{adUnitId}' code={error.Code} → AppOpen"); a.FireOnAdFailedToLoad(error); return; }

            var b = DaroAdInstanceRegistry.Find<DaroBannerAd>(
                DaroAdFormat.Banner, adUnitId);
            if (b != null) { DaroLog.Verbose("Sdk", $"RoutePentaLoadError adUnit='{adUnitId}' code={error.Code} → Banner"); b.FireOnAdFailedToLoad(error); return; }

            var lp = DaroAdInstanceRegistry.Find<DaroLightPopupAd>(
                DaroAdFormat.LightPopup, adUnitId);
            if (lp != null) { DaroLog.Verbose("Sdk", $"RoutePentaLoadError adUnit='{adUnitId}' code={error.Code} → LightPopup"); lp.FireOnAdFailedToLoad(error); return; }

            DaroLog.Verbose("Sdk", $"RoutePentaLoadError adUnit='{adUnitId}' code={error.Code} → no match (registry miss)");
        }

        private static void RouteFullscreenInfo(
            string adUnitId,
            DaroAdInfo info,
            Action<DaroInterstitialAd> onInterstitial,
            Action<DaroRewardedAd>     onRewarded,
            Action<DaroAppOpenAd>      onAppOpen,
            Action<DaroLightPopupAd>   onLightPopup)
        {
            var i = DaroAdInstanceRegistry.Find<DaroInterstitialAd>(
                DaroAdFormat.Interstitial, adUnitId);
            if (i != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenInfo adUnit='{adUnitId}' → Interstitial"); onInterstitial(i); return; }

            var r = DaroAdInstanceRegistry.Find<DaroRewardedAd>(
                DaroAdFormat.Rewarded, adUnitId);
            if (r != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenInfo adUnit='{adUnitId}' → Rewarded"); onRewarded(r); return; }

            var a = DaroAdInstanceRegistry.Find<DaroAppOpenAd>(
                DaroAdFormat.AppOpen, adUnitId);
            if (a != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenInfo adUnit='{adUnitId}' → AppOpen"); onAppOpen(a); return; }

            var lp = DaroAdInstanceRegistry.Find<DaroLightPopupAd>(
                DaroAdFormat.LightPopup, adUnitId);
            if (lp != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenInfo adUnit='{adUnitId}' → LightPopup"); onLightPopup(lp); return; }

            DaroLog.Verbose("Sdk", $"RouteFullscreenInfo adUnit='{adUnitId}' → no match (registry miss)");
        }

        private static void RouteFullscreenDisplayError(string adUnitId, DaroAdDisplayError error)
        {
            var i = DaroAdInstanceRegistry.Find<DaroInterstitialAd>(
                DaroAdFormat.Interstitial, adUnitId);
            if (i != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenDisplayError adUnit='{adUnitId}' code={error.Code} → Interstitial"); i.FireOnAdFailedToShow(error); return; }

            var r = DaroAdInstanceRegistry.Find<DaroRewardedAd>(
                DaroAdFormat.Rewarded, adUnitId);
            if (r != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenDisplayError adUnit='{adUnitId}' code={error.Code} → Rewarded"); r.FireOnAdFailedToShow(error); return; }

            var a = DaroAdInstanceRegistry.Find<DaroAppOpenAd>(
                DaroAdFormat.AppOpen, adUnitId);
            if (a != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenDisplayError adUnit='{adUnitId}' code={error.Code} → AppOpen"); a.FireOnAdFailedToShow(error); return; }

            var lp = DaroAdInstanceRegistry.Find<DaroLightPopupAd>(
                DaroAdFormat.LightPopup, adUnitId);
            if (lp != null) { DaroLog.Verbose("Sdk", $"RouteFullscreenDisplayError adUnit='{adUnitId}' code={error.Code} → LightPopup"); lp.FireOnAdFailedToShow(error); return; }

            DaroLog.Verbose("Sdk", $"RouteFullscreenDisplayError adUnit='{adUnitId}' code={error.Code} → no match (registry miss)");
        }
    }
}
