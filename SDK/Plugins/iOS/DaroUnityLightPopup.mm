//
//  DaroUnityLightPopup.mm
//  Light Popup ObjC++ shim — wraps DaroObjCLightPopupAdLoader + DaroObjCLightPopupAd
//  + DaroObjCLightPopupConfiguration (DaroObjCBridge module) for Unity.
//  Parallel to Android's DaroUnityLightPopupAd.kt; full design in
//  See docs/features/native-bridge.md (Light Popup / iOS).
//
//  Lifecycle (sketch §"Sequence Diagrams — Load → Show → Dismiss"):
//
//    CreateLightPopup    → entry slot + DaroObjCLightPopupAdLoader + delegates
//                          + DaroObjCLightPopupConfiguration baked
//    LoadLightPopup      → clear prior ad/adDelegate + [loader loadAd]
//    IsLightPopupReady   → entry.ad != nil && !entry.destroyed (sync read)
//    ShowLightPopup      → [ad showFrom:UnityGetGLViewController()] (main queue)
//    DestroyLightPopup   → entry.destroyed = YES (dispatch_sync) + nil entry (ARC)
//
//  Configuration apply timing (sketch §"Configuration Apply Sequence"):
//    didLoad delegate → Layer-1 destroyed check → [ad setConfiguration:]
//                     → entry.ad = ad + wire adDelegate → DaroDispatch(adLoaded)
//    Order is critical — consumer may call Show() from OnAdLoaded handler,
//    and show(from:) reads the configuration internally.
//
//  Threading: dictionary mutations on s_adQueue (serial); show/dismiss
//  dispatched to main queue. ViewController-driven callbacks fire on main
//  thread naturally (banner-ios sprint precedent), so DaroDispatch from
//  delegate methods needs no further marshaling.
//
//  3-Layer dispose-race protection (sketch §"Dispose-Race Protection"):
//    Layer 1 — atomic BOOL `destroyed` ivar, dispatch_sync written so the
//              flag is visible to any in-flight delegate callback before
//              the subsequent dispatch_async cleanup runs.
//    Layer 2 — DaroIOSPlatform._disposed (existing, in C#)
//    Layer 3 — DaroLightPopupAd._disposed (existing, in C#)
//

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <DaroObjCBridge/DaroObjCBridge.h>
#import <DaroObjCBridge/DaroObjCBridge-Swift.h>
#import "DaroUnityBridgeInternal.h"
#import "DaroUnityLog.h"

#pragma mark - Forward declarations

@class DaroUnityLightPopupLoaderDelegate;
@class DaroUnityLightPopupAdDelegate;

#pragma mark - DaroUnityLightPopupEntry

// Strong refs to keep loader / ad / delegates / configuration alive (delegate
// properties on DaroObjCLightPopupAdLoader and DaroObjCLightPopupAd are weak).
// `destroyed` is atomic — read on main queue (delegate callbacks) vs. written
// on s_adQueue (DestroyLightPopup) per sketch §"Dispose-Race Protection".
@interface DaroUnityLightPopupEntry : NSObject
@property (nonatomic, strong)            DaroObjCLightPopupAdLoader*       loader;
@property (nonatomic, strong, nullable)  DaroObjCLightPopupAd*             ad;
@property (nonatomic, strong)            DaroUnityLightPopupLoaderDelegate* loaderDelegate;
@property (nonatomic, strong, nullable)  DaroUnityLightPopupAdDelegate*    adDelegate;
@property (nonatomic, strong)            DaroObjCLightPopupConfiguration*  configuration;
@property (atomic,    assign)            BOOL                              destroyed;
@end

@implementation DaroUnityLightPopupEntry
@end

NSMutableDictionary<NSString*, DaroUnityLightPopupEntry*>* s_lightPopups;

#pragma mark - Delegate @interfaces (paired — LoaderDelegate's didLoad creates AdDelegate)

@interface DaroUnityLightPopupLoaderDelegate : NSObject <DaroObjCLightPopupAdLoaderDelegate>
@property (nonatomic, copy) NSString*                  adUnitId;
@property (nonatomic, weak) DaroUnityLightPopupEntry*  entry;
@end

@interface DaroUnityLightPopupAdDelegate : NSObject <DaroObjCLightPopupAdDelegate>
@property (nonatomic, copy) NSString*                  adUnitId;
@property (nonatomic, weak) DaroUnityLightPopupEntry*  entry;
@end

#pragma mark - DaroUnityLightPopupLoaderDelegate

@implementation DaroUnityLightPopupLoaderDelegate

- (void)lightPopupAdLoaderDidLoad:(DaroObjCLightPopupAdLoader*)loader
                               ad:(DaroObjCLightPopupAd*)ad
                           adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"LightPopup", @"loader.didLoad adUnit='%@'", self.adUnitId);
    DaroUnityLightPopupEntry* e = self.entry;
    if (!e || e.destroyed) return;

    // Apply configuration BEFORE dispatching adLoaded — consumer may call Show()
    // immediately from the OnAdLoaded handler, and show(from:) reads config internally.
    [ad setConfiguration:e.configuration];

    DaroUnityLightPopupAdDelegate* adDel = [DaroUnityLightPopupAdDelegate new];
    adDel.adUnitId = self.adUnitId;
    adDel.entry    = e;
    ad.delegate    = adDel;
    e.adDelegate   = adDel;
    e.ad           = ad;

    // ILRD: 통합 브리지는 onPaidEvent 를 광고에 둔다(로더에는 없다). 로드마다
    // 새 광고 객체가 오므로 여기서 per-loaded-ad 로 건다.
    DaroUnityWireRevenue(ad, self.adUnitId, 5);

    NSString* json = @"{\"event\":\"adLoaded\",\"adFormat\":5}";
    DaroDispatch(self.adUnitId, json);
}

- (void)lightPopupAdLoader:(DaroObjCLightPopupAdLoader*)loader
          didFailWithError:(NSError*)error {
    DaroLogW(@"LightPopup", @"loader.didFailWithError adUnit='%@' code=%ld msg='%@'",
             self.adUnitId, (long)error.code, error.localizedDescription);
    DaroUnityLightPopupEntry* e = self.entry;
    if (!e || e.destroyed) return;
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToLoad\",\"adFormat\":5,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

- (void)lightPopupAdLoaderDidClick:(DaroObjCLightPopupAdLoader*)loader
                            adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"LightPopup", @"loader.didClick adUnit='%@'", self.adUnitId);
    DaroUnityLightPopupEntry* e = self.entry;
    if (!e || e.destroyed) return;
    NSString* json = @"{\"event\":\"adClicked\",\"adFormat\":5}";
    DaroDispatch(self.adUnitId, json);
}

- (void)lightPopupAdLoaderDidRecordImpression:(DaroObjCLightPopupAdLoader*)loader
                                       adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"LightPopup", @"loader.didRecordImpression adUnit='%@'", self.adUnitId);
    DaroUnityLightPopupEntry* e = self.entry;
    if (!e || e.destroyed) return;
    NSString* json = @"{\"event\":\"adImpression\",\"adFormat\":5}";
    DaroDispatch(self.adUnitId, json);
}

@end

#pragma mark - DaroUnityLightPopupAdDelegate

@implementation DaroUnityLightPopupAdDelegate

- (void)lightPopupAdDidShow:(DaroObjCLightPopupAd*)ad
                     adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"LightPopup", @"ad.didShow adUnit='%@'", self.adUnitId);
    DaroUnityLightPopupEntry* e = self.entry;
    if (!e || e.destroyed) return;
    NSString* json = @"{\"event\":\"adShown\",\"adFormat\":5}";
    DaroDispatch(self.adUnitId, json);
}

- (void)lightPopupAdDidDismiss:(DaroObjCLightPopupAd*)ad
                        adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"LightPopup", @"ad.didDismiss adUnit='%@'", self.adUnitId);
    DaroUnityLightPopupEntry* e = self.entry;
    if (!e || e.destroyed) return;
    // Clear ad ref — dismissed ad is consumed (parallel to Android ad=null after dismiss).
    e.ad = nil;
    NSString* json = @"{\"event\":\"adDismissed\",\"adFormat\":5}";
    DaroDispatch(self.adUnitId, json);
}

- (void)lightPopupAd:(DaroObjCLightPopupAd*)ad
       didFailToShow:(NSError*)error
              adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogW(@"LightPopup", @"ad.didFailToShow adUnit='%@' code=%ld msg='%@'",
             self.adUnitId, (long)error.code, error.localizedDescription);
    DaroUnityLightPopupEntry* e = self.entry;
    if (!e || e.destroyed) return;
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToShow\",\"adFormat\":5,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroDispatch(self.adUnitId, json);
}

@end

#pragma mark - Helper: build DaroObjCLightPopupConfiguration from 36 float params

// 36 floats = 9 colors × 4 channels (RGBA). Each channel is already pre-divided
// to [0.0, 1.0] at the C# call site (B(byte b) = b / 255f) — no conversion here.
static DaroObjCLightPopupConfiguration* BuildConfig(
    float bgR,        float bgG,        float bgB,        float bgA,
    float containerR, float containerG, float containerB, float containerA,
    float adMarkTextR,float adMarkTextG,float adMarkTextB,float adMarkTextA,
    float adMarkBgR,  float adMarkBgG,  float adMarkBgB,  float adMarkBgA,
    float closeBtnR,  float closeBtnG,  float closeBtnB,  float closeBtnA,
    float titleR,     float titleG,     float titleB,     float titleA,
    float bodyR,      float bodyG,      float bodyB,      float bodyA,
    float ctaBgR,     float ctaBgG,     float ctaBgB,     float ctaBgA,
    float ctaTextR,   float ctaTextG,   float ctaTextB,   float ctaTextA,
    NSString* closeButtonText)
{
    DaroObjCLightPopupConfiguration* c = [DaroObjCLightPopupConfiguration new];
    c.backgroundColor            = [UIColor colorWithRed:bgR        green:bgG        blue:bgB        alpha:bgA];
    c.cardViewBackgroundColor    = [UIColor colorWithRed:containerR green:containerG blue:containerB alpha:containerA];
    c.adMarkLabelTextColor       = [UIColor colorWithRed:adMarkTextR green:adMarkTextG blue:adMarkTextB alpha:adMarkTextA];
    c.adMarkLabelBackgroundColor = [UIColor colorWithRed:adMarkBgR  green:adMarkBgG  blue:adMarkBgB  alpha:adMarkBgA];
    c.closeButtonTextColor       = [UIColor colorWithRed:closeBtnR  green:closeBtnG  blue:closeBtnB  alpha:closeBtnA];
    c.titleTextColor             = [UIColor colorWithRed:titleR     green:titleG     blue:titleB     alpha:titleA];
    c.bodyTextColor              = [UIColor colorWithRed:bodyR      green:bodyG      blue:bodyB      alpha:bodyA];
    c.ctaButtonBackgroundColor   = [UIColor colorWithRed:ctaBgR     green:ctaBgG     blue:ctaBgB     alpha:ctaBgA];
    c.ctaButtonTextColor         = [UIColor colorWithRed:ctaTextR   green:ctaTextG   blue:ctaTextB   alpha:ctaTextA];
    c.closeButtonText            = closeButtonText ?: @"Close";
    return c;
}

#pragma mark - extern C surface (DaroIOSPlatform.cs DllImport targets)

extern "C" {

void DaroUnity_CreateLightPopup(
    const char* adUnitId,
    float bgR,        float bgG,        float bgB,        float bgA,
    float containerR, float containerG, float containerB, float containerA,
    float adMarkTextR,float adMarkTextG,float adMarkTextB,float adMarkTextA,
    float adMarkBgR,  float adMarkBgG,  float adMarkBgB,  float adMarkBgA,
    float closeBtnR,  float closeBtnG,  float closeBtnB,  float closeBtnA,
    float titleR,     float titleG,     float titleB,     float titleA,
    float bodyR,      float bodyG,      float bodyB,      float bodyA,
    float ctaBgR,     float ctaBgG,     float ctaBgB,     float ctaBgA,
    float ctaTextR,   float ctaTextG,   float ctaTextB,   float ctaTextA,
    const char* closeButtonText)
{
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"LightPopup", @"CreateLightPopup adUnit='%@'", unit);


    NSString* closeBtnTextS = closeButtonText
        ? [NSString stringWithUTF8String:closeButtonText] : @"Close";

    // Build configuration on caller frame — UIColor synthesis is cheap and avoids
    // capturing 37 args into the dispatch_async block.
    DaroObjCLightPopupConfiguration* config = BuildConfig(
        bgR, bgG, bgB, bgA,
        containerR, containerG, containerB, containerA,
        adMarkTextR, adMarkTextG, adMarkTextB, adMarkTextA,
        adMarkBgR, adMarkBgG, adMarkBgB, adMarkBgA,
        closeBtnR, closeBtnG, closeBtnB, closeBtnA,
        titleR, titleG, titleB, titleA,
        bodyR, bodyG, bodyB, bodyA,
        ctaBgR, ctaBgG, ctaBgB, ctaBgA,
        ctaTextR, ctaTextG, ctaTextB, ctaTextA,
        closeBtnTextS);

    dispatch_async(s_adQueue, ^{
        // Release any prior entry — duplicate-construction-replaces (parallel to
        // interstitial pattern in DaroUnityBridge.mm).
        s_lightPopups[unit] = nil;

        DaroObjCLightPopupAdLoader* loader =
            [[DaroObjCLightPopupAdLoader alloc] initWithUnitId:unit];

        DaroUnityLightPopupLoaderDelegate* loaderDel =
            [DaroUnityLightPopupLoaderDelegate new];
        loaderDel.adUnitId = unit;

        DaroUnityLightPopupEntry* entry = [DaroUnityLightPopupEntry new];
        entry.loader         = loader;
        entry.configuration  = config;
        entry.loaderDelegate = loaderDel;
        entry.destroyed      = NO;
        loaderDel.entry      = entry;   // weak back-ref — set after entry exists
        loader.delegate      = loaderDel;

        s_lightPopups[unit] = entry;
    });
}

void DaroUnity_LoadLightPopup(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"LightPopup", @"LoadLightPopup adUnit='%@'", unit);
    dispatch_async(s_adQueue, ^{
        DaroUnityLightPopupEntry* entry = s_lightPopups[unit];
        if (!entry || entry.destroyed) return;

        // Re-load: clear prior ad + ad delegate. The same loader instance is reused;
        // calling loadAd() again on the same loader is supported by daro iOS internal
        // (DaroLightPopupAdLoader generates a new cacheKey per call).
        entry.ad         = nil;
        entry.adDelegate = nil;

        [entry.loader loadAd];
    });
}

bool DaroUnity_IsLightPopupReady(const char* adUnitId) {
    if (!adUnitId) return false;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    __block BOOL ready = NO;
    dispatch_sync(s_adQueue, ^{
        DaroUnityLightPopupEntry* entry = s_lightPopups[unit];
        ready = (entry && !entry.destroyed && entry.ad != nil);
    });
    return (bool)ready;
}

void DaroUnity_ShowLightPopup(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"LightPopup", @"ShowLightPopup adUnit='%@'", unit);
    dispatch_async(s_adQueue, ^{
        DaroUnityLightPopupEntry* entry = s_lightPopups[unit];
        if (!entry || entry.destroyed || !entry.ad) return;
        DaroObjCLightPopupAd* ad = entry.ad;
        // show(from:) calls UIViewController.present(...) — main queue required.
        dispatch_async(dispatch_get_main_queue(), ^{
            [ad showFrom:UnityGetGLViewController()];
        });
    });
}

void DaroUnity_DestroyLightPopup(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"LightPopup", @"DestroyLightPopup adUnit='%@'", unit);
    // Layer-1: set destroyed synchronously BEFORE async cleanup so any
    // concurrently-executing delegate callback observes the flag before
    // reaching DaroDispatch. Same intent as Android's @Volatile destroyed=true
    // set synchronously before mainHandler.post.
    dispatch_sync(s_adQueue, ^{
        DaroUnityLightPopupEntry* entry = s_lightPopups[unit];
        if (entry) entry.destroyed = YES;
    });
    dispatch_async(s_adQueue, ^{
        s_lightPopups[unit] = nil;   // ARC releases loader + ad + delegates + config
    });
}

// Sprint native-object-lifecycle-cleanup §DestroyAll hygiene path. Called by
// DaroUnity_DestroyAll (DaroUnityBridge.mm). A2 invariant: set
// entry.destroyed=YES for every live entry BEFORE clearing the dict.
//
// No view removal (D-iOS-lightpopup-modal in plan §2): modal presentation
// lives inside MAX SDK's own controller — force-dismiss during teardown is
// risky w.r.t. MAX internals. Currently presented modal will stay until
// natural dismiss / ARC release after the entry strong-refs (in MAX) drop.
// Mobile hard kill OS-reaps the process; only iOS willTerminate ~5s grace
// has the visual artifact risk (deferred — out of sprint scope).
//
// Caller contract: must NOT be invoked from s_adQueue context — would deadlock.
void DaroUnityLightPopup_DestroyAll(void) {
    dispatch_sync(s_adQueue, ^{
        NSUInteger entryCount = s_lightPopups.count;
        if (entryCount == 0) {
            DaroLogD(@"LightPopup", @"DestroyAll noop (no entries)");
            return;
        }

        for (DaroUnityLightPopupEntry* entry in s_lightPopups.allValues) {
            entry.destroyed = YES;   // A2: armed before dict ref release
        }
        [s_lightPopups removeAllObjects];

        DaroLogD(@"LightPopup", @"DestroyAll cleared %lu entries",
                 (unsigned long)entryCount);
    });
}

}  // extern "C"
