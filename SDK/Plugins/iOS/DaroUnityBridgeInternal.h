//
//  DaroUnityBridgeInternal.h
//  Internal declarations shared between DaroUnityBridge.mm and
//  DaroUnityBannerAd.mm. NOT a public API surface — never include outside
//  these two files.
//
//  Sketch §"File Strategy" + §"DaroUnityBridgeInternal.h" — the shared
//  symbols (s_adQueue, DaroDispatch, EscapeJson, RevenueFields,
//  UnityGetGLViewController) live in DaroUnityBridge.mm, banner code links
//  to them via these extern declarations.
//
#pragma once

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>

NS_ASSUME_NONNULL_BEGIN

@class DaroObjCAdInfo;

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

// `,"valueMicros":N,"currencyCode":"USD","precisionType":N` JSON fragment for
// adRevenuePaid events. Micros cross as an integer — Android does the same, so
// the C# side has one conversion (`FromMicros`) instead of two.
// Defined in DaroUnityBridge.mm.
extern NSString* RevenueFields(int64_t valueMicros,
                               NSString* _Nullable currencyCode,
                               NSInteger precisionType);

// `,"mediationPlatform":"daroa","adNetwork":"AdMob Network"` JSON fragment.
// Emits only the keys it has — the C# reader treats a missing key as null.
// Defined in DaroUnityBridge.mm.
extern NSString* AdInfoFields(DaroObjCAdInfo* _Nullable adInfo);

// 마지막으로 받은 `DaroObjCAdInfo` 를 들고 있는 객체.
//
// 수익 콜백(`onPaidEvent`)은 `DaroObjCAdRevenue` 하나만 받는다 — 그 자리에 미디에이션
// 귀속이 없다. 그래서 이벤트를 받는 델리게이트(또는 엔트리)가 대신 기억해 두고 수익
// 배선이 그것을 읽는다. Android shim 도 같은 이유로 표시 시점 값을 들고 있다.
//
// 프로토콜이 property 를 선언해도 auto-synthesize 는 안 되므로, 채택하는 클래스가
// 자기 @interface 에 같은 property 를 다시 적는다.
@protocol DaroUnityAdInfoHolder <NSObject>
@property (nonatomic, strong, nullable) DaroObjCAdInfo* lastAdInfo;
@end

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

// Attaches the per-instance revenue callback on a unit-routed ad (interstitial /
// rewarded / appOpen / lightPopup); adFormat is the wire format code. `holder`
// supplies the ad info the revenue callback itself does not carry, and is held
// weakly. Banner and native attach their own blocks — both need a
// current-instance guard this helper has no way to express.
// Defined in DaroUnityBridge.mm.
void DaroUnityWireRevenue(id ad,
                          id<DaroUnityAdInfoHolder> _Nullable holder,
                          NSString* adUnitId,
                          NSInteger adFormat);

void DaroUnityNativeAd_DestroyAll(void);
void DaroUnityBanner_DestroyAll(void);
void DaroUnityLightPopup_DestroyAll(void);

// CTA overlay sync (matches DllImport in DaroIOSNativeAdHandle.cs).
// Definitions live in DaroUnityNativeAd.mm inside its extern "C" block.
// 4 floats individually (no marshalled struct) + 1 bool.
void DaroUnity_NativeAd_SetCtaScreenRect(int handleId,
                                          float x, float y, float w, float h,
                                          bool touchEnabled);
void DaroUnity_NativeAd_ClearCtaScreenRect(int handleId);

#ifdef __cplusplus
}
#endif

NS_ASSUME_NONNULL_END
