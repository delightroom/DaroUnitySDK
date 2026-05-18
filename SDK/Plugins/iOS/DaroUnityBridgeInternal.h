//
//  DaroUnityBridgeInternal.h
//  Internal declarations shared between DaroUnityBridge.mm and
//  DaroUnityBannerAd.mm. NOT a public API surface — never include outside
//  these two files.
//
//  Sketch §"File Strategy" + §"DaroUnityBridgeInternal.h" — the shared
//  symbols (s_adQueue, DaroDispatch, EscapeJson, LatencyField,
//  UnityGetGLViewController) live in DaroUnityBridge.mm, banner code links
//  to them via these extern declarations.
//
#pragma once

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>

NS_ASSUME_NONNULL_BEGIN

// Serial queue gating all per-format ad dictionaries (s_interstitials,
// s_rewarded, s_appOpen, and the banner-side s_banners). Defined in
// DaroUnityBridge.mm; created inside its EnsureInitialized().
extern dispatch_queue_t s_adQueue;

// Banner storage. Defined in DaroUnityBannerAd.mm; the banner extern entry
// itself manages this dictionary while DaroUnityBridge.mm bootstraps it.
@class DaroUnityBannerEntry;
extern NSMutableDictionary<NSString*, DaroUnityBannerEntry*>* s_banners;

// Native ad storage. Defined in DaroUnityNativeAd.mm; the native-ad extern
// entries manage this dictionary while DaroUnityBridge.mm bootstraps it.
// Keyed by C#-allocated monotonic int handleId (boxed via NSNumber) — the
// adUnitId-keyed pattern doesn't fit native ad's CD-8 instance-owned model
// (multi-instance same adUnitId).
@class DaroUnityNativeAdEntry;
extern NSMutableDictionary<NSNumber*, DaroUnityNativeAdEntry*>* s_nativeAds;

// Light Popup storage. Defined in DaroUnityLightPopup.mm; the light-popup
// extern entries manage this dictionary while DaroUnityBridge.mm bootstraps
// it. adUnitId-keyed (interstitial-style single-per-adUnitId), unlike native
// ad's instance-keyed model.
@class DaroUnityLightPopupEntry;
extern NSMutableDictionary<NSString*, DaroUnityLightPopupEntry*>* s_lightPopups;

// Single JSON-event channel back to Unity. Defined in DaroUnityBridge.mm.
// adUnitId may be the "__sdk__" sentinel for SDK lifecycle events; banner
// always passes a real ad unit id.
extern void DaroDispatch(NSString* _Nullable adUnitId, NSString* eventJson);

// Minimal JSON string escape — matches DaroUnityBridge.mm's set
// (",", "\\", "\n", "\t", "\r", control chars). Defined in DaroUnityBridge.mm.
extern NSString* EscapeJson(NSString* _Nullable s);

// `,"latency":<num|null>` JSON fragment for ad-info-bearing events. The
// argument is `id` rather than `DaroObjCAdInfo*` to avoid a cross-file
// dependency on the DaroMObjCBridge Swift-generated header at the .h level
// (the .mm definition still casts to DaroObjCAdInfo* internally). Defined
// in DaroUnityBridge.mm.
extern NSString* LatencyField(id _Nullable info);

// Provided by Unity's UnityFramework at link time.
extern UIViewController* UnityGetGLViewController(void);

// Per-format DestroyAll helpers — called by DaroUnity_DestroyAll in
// DaroUnityBridge.mm during app-quit / Unity-runtime-teardown. Each helper
// owns its dict and entry types (entry @interface declarations live in the
// matching .mm file); the dispatcher in DaroUnityBridge.mm cannot access
// those types directly.
//
// Contract per helper: dispatch_sync(s_adQueue) — set entry.destroyed=YES
// for entry-guarded formats BEFORE [dict removeAllObjects]; dispatch_async
// view removal to main queue. Caller (DaroUnity_DestroyAll) must NOT wrap
// these calls in an outer s_adQueue sync block — would deadlock.
//
// `extern "C"` linkage required because the definitions live inside
// `extern "C" { ... }` blocks in each .mm file. ObjC++ default linkage for
// plain `extern void foo()` is C++ — mismatched definition link-fails with
// "Declaration ... has a different language linkage".
//
// See docs/dev/native-object-lifecycle-cleanup/tasks/ios-destroy-all.md
// §DestroyAll path (hygiene) for the helper-dispatcher pattern rationale.
#ifdef __cplusplus
extern "C" {
#endif

void DaroUnityNativeAd_DestroyAll(void);
void DaroUnityBanner_DestroyAll(void);
void DaroUnityLightPopup_DestroyAll(void);

#ifdef __cplusplus
}
#endif

NS_ASSUME_NONNULL_END
