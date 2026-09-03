# Troubleshooting — first-response diagnostics

Ad SDK problems usually fall into one of three categories. Start by deciding which category the symptom belongs to — the rest of this page is organized by category.

| Category | Looks like | Owner of the fix |
|---|---|---|
| **SDK wiring** | `InvalidOperationException` / `ObjectDisposedException`, missing events, slots empty, clicks not firing | Your integration code |
| **Mediation environment** | `OnAdFailedToLoad` with `NoFill` or `InvalidAdUnitIdentifier`, repeated retries on iOS, ATT-impacted fill | Daro / MAX dashboard, ATT prompt, network |
| **Platform build / export** | Build error in Xcode / Gradle, missing pod, IL2CPP link issues, ad unit changes not visible on device | Editor build pipeline, EDM4U |

Most "the ad doesn't show" reports turn out to be **mediation environment** issues that look like SDK bugs. Triage in that order before deep-diving into C# code.

---

## SDK wiring

### `InvalidOperationException` on `Show()` / `Bind()`

The ad wasn't ready. Common causes:
- `Show()` called before `OnAdLoaded` fired. Wait for the event or guard with `IsReady()`.
- `DaroNativeAdView.Bind(ad)` called before `OnAdLoaded`. Move the `Bind` call inside the handler.
- The instance was disposed before the call.

### `ObjectDisposedException` on `Load()` / `Show()` / `Hide()`

The instance was disposed. After `Dispose()`, the only safe operations on most ad types are reading immutable properties (`AdUnitId`) and checking readiness (`IsReady()` / `IsReady`, which returns `false`). Lifecycle calls such as `Load()` / `Show()` / `Hide()` throw. Native ad notification calls (`NotifyVisible()` / `NotifyHidden()` / `NotifyClicked()`) are the exception: they no-op after disposal.

Construct a fresh instance to use a new one.

### Events never fire

Subscribers were registered after `Load()` and got dropped by a fast `OnAdLoaded`. Always register handlers **before** calling `Load()`:

```csharp
_ad = new DaroInterstitialAd(adUnitId);
_ad.OnAdLoaded += info => Debug.Log($"loaded {info.AdUnitId}");            // first
_ad.OnAdFailedToLoad += err => Debug.LogWarning($"load failed: {err.Code}");
_ad.Load();                        // last
```

### `OnSdkInitialized` never fires

Only `OnSdkInitialized` has the late-subscriber contract — subscribing after init still fires once on the main thread. If it never fires, `InitializeAsync()` itself failed or was never called. Add `Debug.Log` around `await DaroSdk.InitializeAsync()` to confirm.

### Empty slots on a Native ad

Walk the [Native common failures](ad-formats/native.md#common-failures) checklist:
1. Is `Bind(ad)` called from inside `OnAdLoaded`?
2. Are the inspector slots wired?
3. Did you set `IconSize` (raw path)? Did you use `view.LoadFor(ad)` (slot path)?

### Native ad click launches but `OnAdClicked` never fires

Raw path: your CTA Button's `onClick` doesn't call `_ad.NotifyClicked()`. Wire it in.

Slot path: this should be automatic via `Bind()`. Check that you're using `view.Bind(ad)` and not just reading `Info` manually.

### Banner stays on screen after scene change

`Dispose()` was never called. Banner is a native overlay; it does not auto-detach with your Canvas or your scene. Add `_banner?.Dispose()` to the screen-owner's `OnDisable` / `OnDestroy`.

### Light Popup color changes aren't applied

`DaroLightPopupAdOptions` is bake-at-construct. Mutating fields after the constructor does nothing. To change colors, `Dispose()` the current instance and construct a new one with the new options.

---

## Mediation environment

### No-fill (`OnAdFailedToLoad` with `Code = NoFill`)

`DaroAdLoadErrorCode.NoFill (204)`. Mediation simply has no inventory for this user / region / ad unit / moment.

- **It's normal.** Production fill rates are never 100%.
- **Backoff and retry.** Do not show a user-facing error on every `NoFill`.
- **Repeated NoFill on iOS Native or Light Popup**: daro iOS retries internally up to ~10 times over ~2 minutes — you may see many `OnAdFailedToLoad` events from a single `Load()`. This is the daro retry pattern, not a bug. Do not add another retry loop on top.

### Invalid ad unit (`InvalidAdUnitIdentifier (-5603)`)

This code is ambiguous on the wire — it covers three different dashboard misconfigurations:

1. The ad unit exists but is **disabled** on the Daro / MAX dashboard.
2. The ad unit exists but **belongs to a different app** (registered to another bundle id).
3. The ad unit is correct but the **app's bundle id doesn't match** the dashboard's registered bundle id (typical after a bundle-id rename or a debug-vs-release id split).

**Always verify the dashboard before debugging in code.** Most repro time on `InvalidAdUnitIdentifier` is wasted in C#. Check:
- The ad unit is enabled.
- The app it's registered under matches your build's bundle id (`Application.identifier`).
- The bundle id whitelist (if your dashboard has one) includes your build's bundle id.

### Consent / GDPR

Daro's privacy settings (`DaroSdk.HasGdprConsent`, `GdprConsentString`, `DoNotSell`, `CcpaConsentString`, `IsTaggedForChildDirectedTreatment`) are pass-through values — they don't display a consent dialog. **Your app is responsible for showing the GDPR / CCPA / UMP UI** and assigning the resulting values before (or during) ad load.

Wrong / missing consent values typically show up as reduced fill, not as a specific error code.

### ATT (iOS App Tracking Transparency)

The ATT prompt is your responsibility — not the SDK's. Without an authorized ATT status, fill rates from networks that rely on IDFA can drop significantly. This is environmental, not an SDK error code.

Use Unity's `com.unity.ads.ios-support` package or your own ATT prompt code to request `Tracking` authorization before initializing the SDK (or at least before relying on ad fill). Verify on a real device — the prompt only fires once per install, and on Test devices it depends on Settings → Privacy → Tracking.

### Repeated retries on iOS Native or Light Popup

As mentioned under No-fill — daro iOS Native ad mediation retries up to ~10 times before giving up. Light Popup is built on the same path, so the pattern propagates. Wait it out; do not "fix" this with publisher-side retries on top.

---

## Platform build / export

### iOS: build fails with "builtin-process-xcframework"

Xcode 16 has tightened code-signature validation. A vendored dynamic xcframework whose embedded vendor certificate has been revoked will fail the `builtin-process-xcframework` step at archive time.

The SDK's Xcode post-process step handles this automatically:
- It strips invalid vendor `_CodeSignature` directories before the build copies the framework.
- The Xcode "CodeSignOnCopy" embed phase then re-signs the framework with your developer certificate.

If you still see a code-signing error:
- Check the Unity Console for a `[Daro:Build]` log line confirming the post-process step ran.
- Confirm `Samples/DaroExample/` (or your own integration) is on a recent SDK version that includes `DaroIosPostProcessor`.
- Make sure your Xcode developer certificate is valid and the project is set up to sign on copy.

### EDM4U / CocoaPods / Android Gradle resolve failures

The SDK declares all native dependencies in `SDK/Editor/DaroDependencies.xml`. EDM4U (External Dependency Manager for Unity) reads that file and resolves the underlying maven artifacts (Android) and pods (iOS). You do not need to add anything to `mainTemplate.gradle` or to a Podfile.

If you see "could not find library" / "pod not found":
1. **Run a force resolve.** Editor menu: **Assets → External Dependency Manager → Android Resolver → Force Resolve** (and the iOS equivalent).
2. **Confirm EDM4U is installed.** It's a hard dependency of the SDK; if your project predates the SDK's import flow it might be missing. The Daro Integration Manager will warn you.
3. **Clean Library/ and reopen Unity.** Stale resolver caches sometimes pin to an older Daro version.

Never hand-edit Gradle or Podfile to "fix" a missing dependency. The SDK's `DaroDependencies.xml` is the canonical source; if it's missing something, the bug is in the SDK manifest, not in your project.

### Settings keys aren't reaching the device

`DaroSettings` (Integration Manager → Daro Settings) holds the Daro `appKey` and related build-time configuration. These are **injected at build time** into `Info.plist` (iOS) and `AndroidManifest.xml` (Android) — they are not passed as a runtime argument to `InitializeAsync()`.

If `OnSdkInitialized` never fires or initialization fails on device:
1. Open the Integration Manager and verify the keys are populated.
2. Build a fresh export (Build Settings → Build, not just Run) and confirm `Info.plist` / `AndroidManifest.xml` in the exported project contain the expected entries.

### Code changes not appearing on device

You changed C# but the device still runs old behavior. Most often a stale Unity export:
- IL2CPP cache, Gradle caches, or Xcode derived data are out of date.
- **First try**: Build Settings → Build (not Run / Build And Run) to refresh the exported project directory cleanly.
- **If that fails**: delete `Library/` and reopen the Unity Editor, then re-export.
- **Verify the build is what you think it is**: log SDK / build versions on startup; compare against the binary on device.

### Ad unit changes on the dashboard not visible

Most mediation dashboards cache for a few minutes. Wait, kill the app fully, and relaunch. If still missing after ~10 minutes, double-check that you saved the change on the right ad unit / app / account.

---

## Format-specific anomaly notes

These are normal behaviors that surprise people — they look like bugs but are correct:

| Symptom | Format | Why it's normal |
|---|---|---|
| `OnAdImpression` fires before the user can have actually seen the ad | Banner, Native, Light Popup (iOS) | iOS fires impression on `didPayRevenue` — the mediation revenue moment. It's a billing event, not a viewability event. |
| `OnAdShown` fires without a manual `Show()` call | Banner | Banner displays by default after `Load()` succeeds; the SDK synthesizes `OnAdShown` after that default display and after later `Hide()` → `Show()`. |
| Many `OnAdFailedToLoad` events from a single `Load()` | Native, Light Popup (iOS) | daro iOS retries internally up to ~10 times. Wait for the inventory to recover. |
| `Info.MediaImage == null` on Android Native | Native (Android v1) | Video / large media is not in v1 scope. |
| Banner refreshes on its own | Banner | Mediation-driven; configured by the dashboard's `refreshInterval`. You do not call `Load()` again. |
| No `OnAdFailedToShow` event on Banner | Banner | Banner has no native show-failure pathway; the only show failure surface is `InvalidOperationException` when `!IsReady()`. |

---

## Support report — what to include

When you escalate an issue, include the following so we don't have to ask:

- **Platform**: Android / iOS.
- **OS version**: e.g. iOS 17.5, Android 14.
- **Device**: e.g. iPhone 15 Pro, Pixel 7.
- **Unity version**: e.g. 6000.0.28f1 (Editor used) + target build (if different).
- **SDK version**: the `so.daro.unity` package version from `Packages/manifest.json` or Integration Manager.
- **Ad format + ad unit id**: mask or last-4 only if sensitive.
- **DaroSettings `appKey`**: first 4 characters only, never the full key.
- **Reproduction steps**: the minimum sequence that triggers the issue.
- **Logs**:
  - Android: `adb logcat -s DaroUnity` (or include the full unfiltered log if you suspect a non-SDK actor).
  - iOS: Xcode console filtered on `[Daro:`.
- **Smoke scenario id** (if applicable): if this is a regression of a known scenario, point to it.

If the symptom is "no fill / no impression / wrong fill rate," include the **Daro / MAX Mediation Debugger** output (in-app debugger menu on the dashboard side) — that's the fastest way for us to disambiguate SDK vs dashboard.

<!-- source: SDK/Editor/DaroDependencies.xml, docs/features/build-integration.md, docs/features/native-bridge.md, .claude/rules/native-deps.md -->
