//
//  DaroUnityBridge.mm
//  Unity ↔ DaroObjCBridge thin shim (sketch CD-1, CD-3, CD-4, CD-5, CD-6, CD-9, CD-13).
//
//  Hosts 21 extern C entry points (init / runtime settings / Interstitial /
//  Rewarded / AppOpen) called from DaroIOSPlatform.cs via
//  [DllImport("__Internal")]. All event delivery happens through a single
//  callback (DaroUnityCallbackFn) emitting flat JSON payloads — see sketch
//  §"Event JSON Schema" for the exact key set.
//
//  Banner is in a peer file: DaroUnityBannerAd.mm (banner-ios sprint).
//  Shared symbols (s_adQueue, DaroDispatch, EscapeJson, RevenueFields,
//  s_banners init) are exposed via DaroUnityBridgeInternal.h.
//
//  Threading: DaroObjCBridge wraps every delegate call in
//  DispatchQueue.main.async, so DaroDispatch runs on the Unity main thread
//  and the C# side does not need to enqueue (sketch CD-5).
//

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <DaroObjCBridge/DaroObjCBridge.h>
#import <DaroObjCBridge/DaroObjCBridge-Swift.h>
#import "DaroUnityBridgeInternal.h"
#import "DaroUnityLog.h"

#pragma mark - Callback channel

typedef void (*DaroUnityCallbackFn)(const char* adUnitId, const char* eventJson);
static DaroUnityCallbackFn s_callback = NULL;

// External linkage — the banner shim (DaroUnityBannerAd.mm) calls this through
// the extern declaration in DaroUnityBridgeInternal.h.
void DaroDispatch(NSString* adUnitId, NSString* eventJson) {
    if (s_callback && eventJson) {
        s_callback(adUnitId ? [adUnitId UTF8String] : "",
                   [eventJson UTF8String]);
    }
}

#pragma mark - JSON helpers

// External linkage — used by DaroUnityBannerAd.mm.
// Minimal escape — only the characters DaroSDK error messages can plausibly
// emit. Avoids pulling in NSJSONSerialization for fixed-shape payloads.
NSString* EscapeJson(NSString* s) {
    if (!s) return @"";
    NSMutableString* out = [NSMutableString stringWithCapacity:s.length];
    for (NSUInteger i = 0; i < s.length; i++) {
        unichar c = [s characterAtIndex:i];
        switch (c) {
            case '"':  [out appendString:@"\\\""]; break;
            case '\\': [out appendString:@"\\\\"]; break;
            case '\n': [out appendString:@"\\n"];  break;
            case '\t': [out appendString:@"\\t"];  break;
            case '\r': [out appendString:@"\\r"];  break;
            default:
                if (c < 0x20) [out appendFormat:@"\\u%04x", c];
                else          [out appendFormat:@"%C", c];
        }
    }
    return out;
}

// External linkage — used by every shim file's didPayRevenue delegate method.
// NSDecimalNumber description with nil locale always renders "." as the
// decimal separator, so the payload stays invariant-culture parseable.
NSString* RevenueFields(NSDecimalNumber* _Nullable value,
                        NSString* _Nullable currencyCode,
                        NSInteger precisionType) {
    return [NSString stringWithFormat:
        @",\"value\":\"%@\",\"currencyCode\":\"%@\",\"precisionType\":%ld",
        EscapeJson(value ? [value descriptionWithLocale:nil] : @"0"),
        EscapeJson(currencyCode.length > 0 ? currencyCode : @"USD"),
        (long)precisionType];
}

#pragma mark - Ad instance container (sketch CD-4)

// DaroObjCBridge does NOT retain ad instances, and ad.delegate is `weak` —
// the shim must hold strong refs to both. Pairing them in one entry means a
// dictionary nil-assignment releases ad + delegate atomically.
@interface DaroUnityAdEntry : NSObject
@property (nonatomic, strong) id ad;
@property (nonatomic, strong) id delegate;
@end
@implementation DaroUnityAdEntry
@end

// External linkage — banner shim queues all dictionary mutations on the same
// serial queue so per-format ordering is preserved.
dispatch_queue_t s_adQueue;
static NSMutableDictionary<NSString*, DaroUnityAdEntry*>* s_interstitials;
static NSMutableDictionary<NSString*, DaroUnityAdEntry*>* s_rewarded;
static NSMutableDictionary<NSString*, DaroUnityAdEntry*>* s_appOpen;

static void EnsureInitialized(void) {
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        s_adQueue       = dispatch_queue_create("so.daro.unity.ads", DISPATCH_QUEUE_SERIAL);
        s_interstitials = [NSMutableDictionary dictionary];
        s_rewarded      = [NSMutableDictionary dictionary];
        s_appOpen       = [NSMutableDictionary dictionary];
        // banner-ios sprint: s_banners is defined in DaroUnityBannerAd.mm
        // (paired with the extern in DaroUnityBridgeInternal.h); init it here
        // so banner extern entry points can assume it exists.
        s_banners       = [NSMutableDictionary dictionary];
        // native-ad-ios sprint: s_nativeAds is defined in DaroUnityNativeAd.mm
        // (paired with the extern in DaroUnityBridgeInternal.h); init it here
        // alongside s_banners. Keyed by NSNumber-boxed handleId (CD-1).
        s_nativeAds     = [NSMutableDictionary dictionary];
        // light-popup-ios sprint: s_lightPopups is defined in DaroUnityLightPopup.mm
        // (paired with the extern in DaroUnityBridgeInternal.h); init it here.
        // adUnitId-keyed (interstitial-style single-per-adUnitId).
        s_lightPopups   = [NSMutableDictionary dictionary];
    });
}

#pragma mark - Delegate adopters

// Each delegate stores its own adUnitId so failure callbacks (which carry no
// adInfo) still know which Unity instance to route to.

@interface DaroUnityInterstitialDelegate : NSObject <DaroObjCInterstitialAdDelegate>
@property (nonatomic, copy) NSString* adUnitId;
@end

@implementation DaroUnityInterstitialDelegate

- (void)interstitialAdDidLoad:(DaroObjCInterstitialAd *)ad
                       adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adLoaded\",\"adFormat\":1}";
    DaroDispatch(self.adUnitId, json);
}

- (void)interstitialAdDidFailToLoad:(DaroObjCInterstitialAd *)ad
                              error:(NSError *)error {
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToLoad\",\"adFormat\":1,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

- (void)interstitialAdDidShow:(DaroObjCInterstitialAd *)ad
                       adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adShown\",\"adFormat\":1}";
    DaroDispatch(self.adUnitId, json);
}

- (void)interstitialAdDidFailToShow:(DaroObjCInterstitialAd *)ad
                             adInfo:(DaroObjCAdInfo * _Nullable)adInfo
                              error:(NSError *)error {
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToShow\",\"adFormat\":1,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

- (void)interstitialAdDidClick:(DaroObjCInterstitialAd *)ad
                        adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adClicked\",\"adFormat\":1}";
    DaroDispatch(self.adUnitId, json);
}

- (void)interstitialAdDidRecordImpression:(DaroObjCInterstitialAd *)ad
                                   adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adImpression\",\"adFormat\":1}";
    DaroDispatch(self.adUnitId, json);
}

- (void)interstitialAdDidDismiss:(DaroObjCInterstitialAd *)ad
                          adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adDismissed\",\"adFormat\":1}";
    DaroDispatch(self.adUnitId, json);
}

@end

@interface DaroUnityRewardedDelegate : NSObject <DaroObjCRewardedAdDelegate>
@property (nonatomic, copy) NSString* adUnitId;
@end

@implementation DaroUnityRewardedDelegate

- (void)rewardedAdDidLoad:(DaroObjCRewardedAd *)ad
                   adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adLoaded\",\"adFormat\":2}";
    DaroDispatch(self.adUnitId, json);
}

- (void)rewardedAdDidFailToLoad:(DaroObjCRewardedAd *)ad
                          error:(NSError *)error {
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToLoad\",\"adFormat\":2,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

- (void)rewardedAdDidShow:(DaroObjCRewardedAd *)ad
                   adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adShown\",\"adFormat\":2}";
    DaroDispatch(self.adUnitId, json);
}

- (void)rewardedAdDidFailToShow:(DaroObjCRewardedAd *)ad
                         adInfo:(DaroObjCAdInfo * _Nullable)adInfo
                          error:(NSError *)error {
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToShow\",\"adFormat\":2,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

- (void)rewardedAdDidEarnReward:(DaroObjCRewardedAd *)ad
                         adInfo:(DaroObjCAdInfo * _Nullable)adInfo
                   rewardedItem:(DaroObjCRewardedItem *)item {
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"earnedReward\",\"adFormat\":2,\"rewardAmount\":%ld,\"rewardType\":\"%@\"}",
        (long)item.amount, EscapeJson(item.rewardType)];
    DaroDispatch(self.adUnitId, json);
}

- (void)rewardedAdDidClick:(DaroObjCRewardedAd *)ad
                    adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adClicked\",\"adFormat\":2}";
    DaroDispatch(self.adUnitId, json);
}

- (void)rewardedAdDidRecordImpression:(DaroObjCRewardedAd *)ad
                               adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adImpression\",\"adFormat\":2}";
    DaroDispatch(self.adUnitId, json);
}

- (void)rewardedAdDidDismiss:(DaroObjCRewardedAd *)ad
                      adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adDismissed\",\"adFormat\":2}";
    DaroDispatch(self.adUnitId, json);
}

@end

@interface DaroUnityAppOpenDelegate : NSObject <DaroObjCAppOpenAdDelegate>
@property (nonatomic, copy) NSString* adUnitId;
@end

@implementation DaroUnityAppOpenDelegate

- (void)appOpenAdDidLoad:(DaroObjCAppOpenAd *)ad
                  adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adLoaded\",\"adFormat\":4}";
    DaroDispatch(self.adUnitId, json);
}

- (void)appOpenAdDidFailToLoad:(DaroObjCAppOpenAd *)ad
                         error:(NSError *)error {
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToLoad\",\"adFormat\":4,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

- (void)appOpenAdDidShow:(DaroObjCAppOpenAd *)ad
                  adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adShown\",\"adFormat\":4}";
    DaroDispatch(self.adUnitId, json);
}

- (void)appOpenAdDidFailToShow:(DaroObjCAppOpenAd *)ad
                        adInfo:(DaroObjCAdInfo * _Nullable)adInfo
                         error:(NSError *)error {
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToShow\",\"adFormat\":4,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

- (void)appOpenAdDidClick:(DaroObjCAppOpenAd *)ad
                   adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adClicked\",\"adFormat\":4}";
    DaroDispatch(self.adUnitId, json);
}

- (void)appOpenAdDidRecordImpression:(DaroObjCAppOpenAd *)ad
                              adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adImpression\",\"adFormat\":4}";
    DaroDispatch(self.adUnitId, json);
}

- (void)appOpenAdDidDismiss:(DaroObjCAppOpenAd *)ad
                     adInfo:(DaroObjCAdInfo * _Nullable)adInfo {
    NSString* json = @"{\"event\":\"adDismissed\",\"adFormat\":4}";
    DaroDispatch(self.adUnitId, json);
}

@end

#pragma mark - extern C surface

extern "C" {

#pragma mark · Callback registration

void DaroUnity_SetCallback(DaroUnityCallbackFn callback) {
    s_callback = callback;
}

#pragma mark · Paid event (ILRD)

// External linkage — used by every unit-routed creation site (incl. the other
// shim files) to attach the per-instance revenue callback. Native is
// handle-routed and attaches its own block (DaroUnityNativeAd.mm).
//
// 통합 SDK 는 광고를 만드는 시점에 스스로 훅을 걸고 소비자에겐 net 값만
// 내린다 — 능력을 증명해 훅을 얻던 절차가 없어져 토큰 유도도 사라졌다.
// 콜백이 받는 것은 DaroObjCAdRevenue 하나뿐이다.
void DaroUnityWireRevenue(id ad, NSString* adUnitId, NSInteger adFormat) {
    if (!ad) return;
    [ad setOnPaidEvent:^(DaroObjCAdRevenue* revenue) {
        DaroDispatch(adUnitId, [NSString stringWithFormat:
            @"{\"event\":\"adRevenuePaid\",\"adFormat\":%ld%@}",
            (long)adFormat,
            RevenueFields(revenue.value, revenue.currencyCode, revenue.precision)]);
    }];
}

#pragma mark · SDK lifecycle

void DaroUnity_Initialize(int hasGdprConsent,
                          const char* gdprConsentString,
                          int doNotSell,
                          const char* ccpaConsentString,
                          int isTaggedForCoppa,
                          int logLevel)
{
    EnsureInitialized();

    DaroObjCAds* ads = [DaroObjCAds shared];
    if (hasGdprConsent >= 0) ads.hasUserConsent    = @(hasGdprConsent == 1);
    if (gdprConsentString)   ads.gdprConsentString = [NSString stringWithUTF8String:gdprConsentString];
    if (doNotSell      >= 0) ads.doNotSell         = @(doNotSell == 1);
    if (ccpaConsentString)   ads.ccpaString        = [NSString stringWithUTF8String:ccpaConsentString];
    // 통합 SDK 는 `setCoppa:` 를 미디에이션 변종과 무관하게 노출한다 — 구
    // DaroMObjCBridge 에서 이 심볼이 `#if DARO_ADDMOB` 안에만 있어 MAX 변종이
    // 값을 버려야 했던 제약(CD-12)이 사라졌다. 이제 Android 와 같이 전달한다.
    //
    // DaroCoppaStatus 는 child / notChild 둘뿐이라 C# 의 tri-state 중 null 을
    // 표현할 값이 없다. null 이면 호출하지 않고 SDK 기본값에 맡긴다 — 아무 말도
    // 안 한 퍼블리셔를 대신해 "아동 대상 아님"을 단언하지 않는다.
    if (isTaggedForCoppa >= 0) {
        [ads setCoppa:(isTaggedForCoppa == 1 ? DaroCoppaStatusChild
                                             : DaroCoppaStatusNotChild)];
    }
    // log-module-ios: raw `Daro.DaroLogLevel` int (0..4) crosses the boundary;
    // collapse here for daro iOS internal `DaroObjCLogLevel`. Shim-side gate
    // keeps full granularity in `gDaroUnityLogLevel`.
    DaroUnityLogSetLevel(logLevel);
    ads.logLevel = (DaroObjCLogLevel)DaroUnityCollapseToObjCLogLevel(logLevel);

    [ads initializeWithCompletion:^(NSError* _Nullable error) {
        NSString* json;
        if (error) {
            json = [NSString stringWithFormat:
                @"{\"event\":\"sdkInitFailed\",\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
                (long)error.code, EscapeJson(error.localizedDescription)];
        } else {
            json = @"{\"event\":\"sdkInitialized\"}";
        }
        DaroDispatch(@"__sdk__", json);
    }];
}

#pragma mark · Runtime settings

void DaroUnity_SetUserId(const char* userId) {
    [DaroObjCAds shared].userId = userId ? [NSString stringWithUTF8String:userId] : nil;
}

void DaroUnity_SetAppMuted(bool muted) {
    [[DaroObjCAds shared] setAppMuted:(BOOL)muted];
}

void DaroUnity_SetLogLevel(int level) {
    DaroUnityLogSetLevel(level);
    [DaroObjCAds shared].logLevel = (DaroObjCLogLevel)DaroUnityCollapseToObjCLogLevel(level);
}

#pragma mark · Interstitial

void DaroUnity_CreateInterstitial(const char* adUnitId) {
    EnsureInitialized();
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];

    dispatch_async(s_adQueue, ^{
        // Replace any existing entry — duplicate-construction-replaces (sketch §CD-4).
        s_interstitials[unit] = nil;

        DaroUnityInterstitialDelegate* delegate = [DaroUnityInterstitialDelegate new];
        delegate.adUnitId = unit;
        DaroObjCInterstitialAd* ad = [[DaroObjCInterstitialAd alloc] initWithAdUnitId:unit];
        ad.delegate = delegate;
        DaroUnityWireRevenue(ad, unit, 1);

        DaroUnityAdEntry* entry = [DaroUnityAdEntry new];
        entry.ad = ad;
        entry.delegate = delegate;
        s_interstitials[unit] = entry;
    });
}

void DaroUnity_LoadInterstitial(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroObjCInterstitialAd* ad = (DaroObjCInterstitialAd*)s_interstitials[unit].ad;
        if (ad) [ad load];
    });
}

bool DaroUnity_IsInterstitialReady(const char* adUnitId) {
    if (!adUnitId) return false;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    __block BOOL ready = NO;
    dispatch_sync(s_adQueue, ^{
        DaroObjCInterstitialAd* ad = (DaroObjCInterstitialAd*)s_interstitials[unit].ad;
        ready = ad ? ad.isReady : NO;
    });
    return (bool)ready;
}

void DaroUnity_ShowInterstitial(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroObjCInterstitialAd* ad = (DaroObjCInterstitialAd*)s_interstitials[unit].ad;
        if (!ad) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            [ad showFrom:UnityGetGLViewController()];
        });
    });
}

void DaroUnity_DestroyInterstitial(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        s_interstitials[unit] = nil;  // ARC releases ad + delegate together
    });
}

#pragma mark · Rewarded

void DaroUnity_CreateRewarded(const char* adUnitId) {
    EnsureInitialized();
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];

    dispatch_async(s_adQueue, ^{
        s_rewarded[unit] = nil;

        DaroUnityRewardedDelegate* delegate = [DaroUnityRewardedDelegate new];
        delegate.adUnitId = unit;
        DaroObjCRewardedAd* ad = [[DaroObjCRewardedAd alloc] initWithAdUnitId:unit];
        ad.delegate = delegate;
        DaroUnityWireRevenue(ad, unit, 2);

        DaroUnityAdEntry* entry = [DaroUnityAdEntry new];
        entry.ad = ad;
        entry.delegate = delegate;
        s_rewarded[unit] = entry;
    });
}

void DaroUnity_LoadRewarded(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroObjCRewardedAd* ad = (DaroObjCRewardedAd*)s_rewarded[unit].ad;
        if (ad) [ad load];
    });
}

bool DaroUnity_IsRewardedReady(const char* adUnitId) {
    if (!adUnitId) return false;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    __block BOOL ready = NO;
    dispatch_sync(s_adQueue, ^{
        DaroObjCRewardedAd* ad = (DaroObjCRewardedAd*)s_rewarded[unit].ad;
        ready = ad ? ad.isReady : NO;
    });
    return (bool)ready;
}

void DaroUnity_ShowRewarded(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroObjCRewardedAd* ad = (DaroObjCRewardedAd*)s_rewarded[unit].ad;
        if (!ad) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            [ad showFrom:UnityGetGLViewController()];
        });
    });
}

void DaroUnity_DestroyRewarded(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        s_rewarded[unit] = nil;
    });
}

void DaroUnity_SetRewardedCustomData(const char* adUnitId, const char* customData) {
    if (!adUnitId || !customData) return;
    NSString* unit       = [NSString stringWithUTF8String:adUnitId];
    NSString* customDataS = [NSString stringWithUTF8String:customData];
    dispatch_async(s_adQueue, ^{
        DaroObjCRewardedAd* ad = (DaroObjCRewardedAd*)s_rewarded[unit].ad;
        if (ad) [ad setCustomData:customDataS];
    });
}

#pragma mark · AppOpen

void DaroUnity_CreateAppOpen(const char* adUnitId) {
    EnsureInitialized();
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];

    dispatch_async(s_adQueue, ^{
        s_appOpen[unit] = nil;

        DaroUnityAppOpenDelegate* delegate = [DaroUnityAppOpenDelegate new];
        delegate.adUnitId = unit;
        DaroObjCAppOpenAd* ad = [[DaroObjCAppOpenAd alloc] initWithAdUnitId:unit];
        ad.delegate = delegate;
        DaroUnityWireRevenue(ad, unit, 4);

        DaroUnityAdEntry* entry = [DaroUnityAdEntry new];
        entry.ad = ad;
        entry.delegate = delegate;
        s_appOpen[unit] = entry;
    });
}

void DaroUnity_LoadAppOpen(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroObjCAppOpenAd* ad = (DaroObjCAppOpenAd*)s_appOpen[unit].ad;
        if (ad) [ad load];
    });
}

bool DaroUnity_IsAppOpenReady(const char* adUnitId) {
    if (!adUnitId) return false;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    __block BOOL ready = NO;
    dispatch_sync(s_adQueue, ^{
        DaroObjCAppOpenAd* ad = (DaroObjCAppOpenAd*)s_appOpen[unit].ad;
        ready = ad ? ad.isReady : NO;
    });
    return (bool)ready;
}

void DaroUnity_ShowAppOpen(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroObjCAppOpenAd* ad = (DaroObjCAppOpenAd*)s_appOpen[unit].ad;
        if (!ad) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            // AppOpen `show` takes no UIViewController argument (verified
            // against DaroObjCAppOpenAd in DaroObjCBridge-Swift.h).
            [ad show];
        });
    });
}

void DaroUnity_DestroyAppOpen(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        s_appOpen[unit] = nil;
    });
}

// Sprint native-object-lifecycle-cleanup §DestroyAll hygiene path. Called
// from C# `DaroIOSPlatform.DestroyAll` (csharp-runtime-hook task wires the
// DllImport). Runs on app-quit / Unity-runtime-teardown after C# side has
// already set `DaroAdInstanceRegistry._isShuttingDown=true` — so even
// in-flight callbacks would null-route through the C# Find gate. This
// function actively releases native resources (dict entries, attached views)
// to limit grace-period background work + visual artifacts.
//
// Per goal §Best-effort: hygiene tier, not production-critical (mobile hard
// kill is OS-reaped). Helper-dispatcher pattern (plan decision 2026-05-14):
// fullscreen dicts cleared here directly; entry-aware formats delegate to
// per-shim-file helpers declared in DaroUnityBridgeInternal.h.
//
// Caller contract: must NOT be invoked from s_adQueue context — would deadlock
// (this function's own dispatch_sync + each helper's dispatch_sync). C#
// DllImport callers run on Unity main / worker, never on s_adQueue.
void DaroUnity_DestroyAll(void) {
    EnsureInitialized();   // defensive — quit hook may fire before any ad
                            // create (e.g. user quits before first
                            // Initialize / ad request)
    DaroLogD(@"Bridge", @"DestroyAll start");

    // Fullscreen formats: dict-nil only (D-iOS-fullscreen-skip in plan §2).
    // No entry-level destroyed flag — delegates hold adUnitId, not entry
    // state, so late callbacks are covered by C# DaroAdInstanceRegistry
    // Find gate (set by caller before this function runs).
    dispatch_sync(s_adQueue, ^{
        NSUInteger fullscreenCount =
            s_interstitials.count + s_rewarded.count + s_appOpen.count;
        [s_interstitials removeAllObjects];
        [s_rewarded      removeAllObjects];
        [s_appOpen       removeAllObjects];
        DaroLogD(@"Bridge", @"DestroyAll cleared %lu fullscreen entries",
                 (unsigned long)fullscreenCount);
    });

    // Entry-guarded / view-attached formats: delegate to per-shim helpers.
    // Each helper takes its own s_adQueue dispatch_sync — DO NOT wrap these
    // in an outer sync block (re-entrant deadlock).
    //
    // Per-helper @try/@catch so one helper throwing (NSException from a
    // misbehaving entry during teardown) doesn't skip the rest. Same
    // best-effort hygiene contract as the Android Dispose() per-object
    // isolation. Failures emit via DaroLogW and continue.
    @try { DaroUnityNativeAd_DestroyAll(); }
    @catch (NSException* e) {
        DaroLogW(@"Bridge", @"NativeAd_DestroyAll threw: %@ — continuing", e.reason ?: e.name);
    }
    @try { DaroUnityBanner_DestroyAll(); }
    @catch (NSException* e) {
        DaroLogW(@"Bridge", @"Banner_DestroyAll threw: %@ — continuing", e.reason ?: e.name);
    }
    @try { DaroUnityLightPopup_DestroyAll(); }
    @catch (NSException* e) {
        DaroLogW(@"Bridge", @"LightPopup_DestroyAll threw: %@ — continuing", e.reason ?: e.name);
    }

    DaroLogD(@"Bridge", @"DestroyAll complete");
}

}  // extern "C"
