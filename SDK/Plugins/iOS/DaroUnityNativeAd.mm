//
//  DaroUnityNativeAd.mm
//  Native ad ObjC++ shim — wraps DaroObjCNativeView (DaroMObjCBridge module)
//  for Unity. Parallel to Android's DaroUnityNativeAd.kt; full design in
//  docs/dev/native-ad-ios/sketch-native-ad-ios.md.
//
//  Lifecycle (sketch §5):
//    Create        → entry slot only, no view yet
//    Load          → construct host UIView (alpha=0, hitTest:nil) +
//                    DaroObjCNativeView (autoLoad=NO) + bound view tree +
//                    bindNativeViews + loadNativeAd
//    NotifyVisible → log only (v1 parity with Android signature)
//    NotifyHidden  → log only
//    NotifyClicked → dispatch hidden CTA UIButton sendActions(.touchUpInside)
//    Destroy       → Layer-1 destroyed=YES + view removeFromSuperview + dict nil
//
//  Multi-instance (CD-1, CD-8): handleId-keyed s_nativeAds NSDictionary;
//  same adUnitId across N handles yields N independent entries.
//
//  Threading: dictionary mutations on s_adQueue (serial); UIView ops dispatched
//  to dispatch_get_main_queue. DaroMObjCBridge guarantees delegate callbacks
//  on the main queue (sketch CD-7 + DaroUnityBridge.mm:14-17), so emit-side
//  s_nativeAdCallback invocations need no further marshaling.
//
//  Asset transport (CD-2): dedicated callback channel
//    void(*)(int handleId, const char* eventJson, const uint8_t* iconPng, int iconLen)
//  carries PNG bytes on adLoaded; NULL/0 on every other event.
//

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <DaroMObjCBridge/DaroMObjCBridge.h>
#import <DaroMObjCBridge/DaroMObjCBridge-Swift.h>
#import "DaroUnityBridgeInternal.h"
#import "DaroUnityLog.h"

#pragma mark - Forward declarations

@class DaroUnityNativeAdDelegate;
@class DaroUnityNativeAdHost;

#pragma mark - Callback channel (CD-2)

typedef void (*DaroNativeAdCallbackFn)(int handleId,
                                       const char* eventJson,
                                       const uint8_t* iconPng,
                                       int iconLen);
static DaroNativeAdCallbackFn s_nativeAdCallback = NULL;

#pragma mark - Entry, delegate, host

// Per-instance entry — strong refs survive ARC drop until s_nativeAds[id] = nil.
@interface DaroUnityNativeAdEntry : NSObject
@property (nonatomic, copy)   NSString*                                  adUnitId;
@property (nonatomic, assign) int                                        handleId;
@property (nonatomic, strong, nullable) DaroObjCNativeView*              nativeView;
@property (nonatomic, strong, nullable) DaroUnityNativeAdDelegate*       delegate;
@property (nonatomic, strong, nullable) DaroUnityNativeAdHost*           host;

// Bound view tree — created sync in Load, passed to bindNativeViews. daro-m
// fills these during renderAd (sync, before listener fires per
// CommonAdNativeView.swift:185-187).
@property (nonatomic, strong, nullable) UILabel*     titleLabel;
@property (nonatomic, strong, nullable) UILabel*     bodyLabel;
@property (nonatomic, strong, nullable) UIImageView* iconImageView;
@property (nonatomic, strong, nullable) UIButton*    callToActionButton;
@property (nonatomic, strong, nullable) UIView*      mediaContentView;

// CD-9 Layer-1 guard — atomic for cross-queue write/read safety. Set on
// destroy; checked at top of every delegate method, every extern C body
// after entry lookup, every dispatch_async closure, and inside the icon
// scrape recursion.
@property (atomic, assign) BOOL destroyed;

// Order-fix queue. MAX's didPayRevenue fires synchronously during renderAd
// (CommonAdNativeView.swift:185), which runs BEFORE listener.onAdLoadSuccess
// (line 187) — so nativeViewDidRecordImpression runs BEFORE nativeViewDidLoad.
// We queue every impression unconditionally; scrapeAndDeliver flushes it
// after adLoaded emits. Impressions are 1:1 with successful renders so the
// queue depth stays at 0 or 1 and the queued impression always belongs to the
// in-flight load (memory: feedback_daro_ios_impression_revenue_gating.md).
// loadedEmitted retained as a diagnostic / future hook — currently unused
// in the emission path but resets per cycle so refresh-driven loads (which
// bypass DaroUnity_NativeAd_Load) start clean.
@property (atomic, assign)              BOOL              loadedEmitted;
@property (nonatomic, strong, nullable) DaroObjCAdInfo*   pendingImpression;
@end

@implementation DaroUnityNativeAdEntry
@end

// CD-4 touch-blocking host. UIView subclass with hitTest:withEvent:→nil
// override + isUserInteractionEnabled=NO (set on instance). Defense in depth
// — Android Finding A iOS parity. UIControl `sendActions:` bypasses
// hit-testing so the click forward path survives this block.
@interface DaroUnityNativeAdHost : UIView
@end
@implementation DaroUnityNativeAdHost
- (UIView*)hitTest:(CGPoint)point withEvent:(UIEvent*)event { return nil; }
@end

// 4-method delegate adopter (DaroObjCNativeView's @objc optional protocol —
// DaroObjCNativeView.swift:11-16).
@interface DaroUnityNativeAdDelegate : NSObject <DaroObjCNativeViewDelegate>
@property (nonatomic, weak) DaroUnityNativeAdEntry* entry;   // weak — entry owns delegate strong
- (void)scrapeAndDeliver:(DaroUnityNativeAdEntry*)entry
                  adInfo:(DaroObjCAdInfo*)info
                 attempt:(int)attempt;
@end

#pragma mark - Storage (definition; declared extern in DaroUnityBridgeInternal.h)

NSMutableDictionary<NSNumber*, DaroUnityNativeAdEntry*>* s_nativeAds = nil;

#pragma mark - Polling constants — Android shim parity

// Poll budget for MAX's image fetch. iOS adapters typically resolve icon
// synchronously (image non-nil at delegate fire time), but URL-based
// adapters may not — carry Android Glide's 5×200ms safety budget for the
// slow-adapter case. Falls through with NULL icon when retries exhaust
// (degraded but non-blocking).
static const int    kIconPollMaxAttempts = 5;
static const double kIconPollIntervalSec = 0.2;

#pragma mark - Delegate adopter implementation

@implementation DaroUnityNativeAdDelegate

- (void)nativeViewDidLoad:(DaroObjCNativeView*)view
                   adInfo:(DaroObjCAdInfo*)adInfo {
    if (self.entry.destroyed) return;

    // Order-fix: reset loadedEmitted at the start of EVERY load delivery —
    // covers daro-internal refresh-driven loads (CommonAdNativeView's
    // coordinator.refreshHandler → loadAd) which bypass our
    // DaroUnity_NativeAd_Load entry point. DO NOT clear pendingImpression
    // here: MAX's didPayRevenue fires synchronously during renderAd
    // (CommonAdNativeView.swift:185), which runs BEFORE listener.onAdLoadSuccess
    // (line 187, which triggers this delegate). So pendingImpression at this
    // point was queued for THIS cycle and must survive the reset to flush
    // after adLoaded emits.
    self.entry.loadedEmitted = NO;

    [self scrapeAndDeliver:self.entry adInfo:adInfo attempt:0];
}

- (void)nativeView:(DaroObjCNativeView*)view
  didFailWithError:(NSError*)error {
    if (self.entry.destroyed) return;
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToLoad\",\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    if (s_nativeAdCallback) {
        s_nativeAdCallback(self.entry.handleId, [json UTF8String], NULL, 0);
    }
}

- (void)nativeViewDidClick:(DaroObjCNativeView*)view
                    adInfo:(DaroObjCAdInfo*)adInfo {
    if (self.entry.destroyed) return;
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adClicked\"%@}", LatencyField(adInfo)];
    if (s_nativeAdCallback) {
        s_nativeAdCallback(self.entry.handleId, [json UTF8String], NULL, 0);
    }
}

- (void)nativeViewDidRecordImpression:(DaroObjCNativeView*)view
                               adInfo:(DaroObjCAdInfo*)adInfo {
    if (self.entry.destroyed) return;

    // Order-fix: ALWAYS queue impression. MAX's didPayRevenue fires
    // synchronously during renderAd (CommonAdNativeView.swift:185), which
    // runs BEFORE listener.onAdLoadSuccess (line 187) — so this delegate
    // method runs BEFORE nativeViewDidLoad. Gating on loadedEmitted breaks
    // on refresh cycles where loadedEmitted=YES carries over from cycle N-1.
    // Impressions are 1:1 with renders and renders are 1:1 with successful
    // loads, so the queue depth stays at 0 or 1 and the queued impression
    // always belongs to the in-flight load. scrapeAndDeliver flushes it
    // after adLoaded emits.
    self.entry.pendingImpression = adInfo;
}

// CD-6 icon scrape with 5×200ms polling fallback. iOS MAX adapters mostly
// resolve icon synchronously (image is non-nil at delegate fire time), but
// URL-based adapters (rare) may not — parity with Android Glide polling.
- (void)scrapeAndDeliver:(DaroUnityNativeAdEntry*)entry
                  adInfo:(DaroObjCAdInfo*)info
                 attempt:(int)attempt {
    if (entry.destroyed) return;

    UIImage* image = entry.iconImageView.image;
    if (!image && attempt < kIconPollMaxAttempts) {
        __weak DaroUnityNativeAdDelegate* weakSelf = self;
        dispatch_after(
            dispatch_time(DISPATCH_TIME_NOW,
                          (int64_t)(kIconPollIntervalSec * NSEC_PER_SEC)),
            dispatch_get_main_queue(), ^{
                [weakSelf scrapeAndDeliver:entry adInfo:info attempt:attempt + 1];
            });
        return;
    }

    NSData* png = image ? UIImagePNGRepresentation(image) : nil;
    NSString* title = entry.titleLabel.text ?: @"";
    NSString* body  = entry.bodyLabel.text  ?: @"";
    NSString* cta   = entry.callToActionButton.titleLabel.text ?: @"";

    // Bug #2 debug: scrape result. If title/body/cta empty + image nil →
    // daro-m didn't populate our loose UILabels (likely needs them as
    // subviews of maNativeAdView for tag-based binder to find them).
    DaroLogD(@"Native", @"scrape h=%d title='%@' body='%@' cta='%@' image=%@ icon=%@",
             entry.handleId,
             title, body, cta,
             image ? [NSString stringWithFormat:@"%dx%d", (int)image.size.width, (int)image.size.height] : @"nil",
             png ? [NSString stringWithFormat:@"%dB", (int)png.length] : @"nil");

    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adLoaded\"%@,\"title\":\"%@\",\"body\":\"%@\",\"callToAction\":\"%@\"}",
        LatencyField(info),
        EscapeJson(title), EscapeJson(body), EscapeJson(cta)];

    if (s_nativeAdCallback) {
        // png lifetime: NSData is autoreleased; the synchronous PInvoke call
        // returns before the autorelease pool drains, so png.bytes is valid
        // for the duration of the callback. C# Marshal.Copy's to a managed
        // byte[] before returning. Do NOT refactor this call to dispatch_async
        // — that would let the autorelease pool drain first and dangle the
        // pointer.
        //
        // ObjC++ note: NSData.bytes returns `const void*`, which ObjC++
        // (.mm) refuses to implicitly convert to `const uint8_t*` (unlike
        // ObjC .m). Explicit cast required.
        const uint8_t* iconBytes = png ? (const uint8_t*)png.bytes : nullptr;
        int            iconLen   = png ? (int)png.length : 0;
        s_nativeAdCallback(entry.handleId, [json UTF8String], iconBytes, iconLen);
    }

    // Order-fix: mark adLoaded emitted; flush any impression that arrived
    // during the scrape polling window (would otherwise have beaten
    // adLoaded on the wire — Daro iOS fires impression on revenue paid,
    // ~5ms after onAdLoadSuccess, while polling defers up to 1s).
    entry.loadedEmitted = YES;
    DaroObjCAdInfo* pending = entry.pendingImpression;
    entry.pendingImpression = nil;
    if (pending && s_nativeAdCallback) {
        NSString* impressionJson = [NSString stringWithFormat:
            @"{\"event\":\"adImpression\"%@}", LatencyField(pending)];
        s_nativeAdCallback(entry.handleId, [impressionJson UTF8String], NULL, 0);
    }
}

@end

#pragma mark - extern C surface (matches [DllImport] in DaroIOSNativeAdHandle.cs)

extern "C" {

void DaroUnity_NativeAd_SetCallback(DaroNativeAdCallbackFn callback) {
    s_nativeAdCallback = callback;
}

void DaroUnity_NativeAd_Create(int handleId, const char* adUnitId, const char* placement) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    NSNumber* key  = @(handleId);
    (void)placement;   // v1 parity with DaroUnity_CreateInterstitial — placement
                       // accepted for ABI uniformity, dropped (DaroObjCNativeView
                       // exposes no placement setter at the ObjC layer).

    dispatch_async(s_adQueue, ^{
        // Replace any existing entry — duplicate-construction-replaces.
        s_nativeAds[key] = nil;

        DaroUnityNativeAdEntry* entry = [DaroUnityNativeAdEntry new];
        entry.adUnitId = unit;
        entry.handleId = handleId;

        DaroUnityNativeAdDelegate* delegate = [DaroUnityNativeAdDelegate new];
        delegate.entry = entry;
        entry.delegate = delegate;

        s_nativeAds[key] = entry;
    });
}

void DaroUnity_NativeAd_Load(int handleId, int iconWidth, int iconHeight) {
    NSNumber* key = @(handleId);
    // Clamp ≥1 — image fetcher cache sizing rejects 0×0; host needs > 0.
    int hostWidth  = (iconWidth  > 0) ? iconWidth  : 1;
    int hostHeight = (iconHeight > 0) ? iconHeight : 1;

    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry || entry.destroyed) return;

        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;

            // Order-fix: reset per-Load flags so the next Load lifecycle
            // starts with no queued impression / loadedEmitted=NO.
            entry.loadedEmitted     = NO;
            entry.pendingImpression = nil;

            UIViewController* vc = UnityGetGLViewController();
            if (!vc) return;   // Unity not ready — silently bail (Banner parity)

            // CD-4 + CD-5: hidden host — both touch-blocking layers + on-screen
            // window-attached frame so MAX impression-viewability check passes.
            DaroUnityNativeAdHost* host = [[DaroUnityNativeAdHost alloc]
                initWithFrame:CGRectMake(0, 0, hostWidth, hostHeight)];
            host.alpha = 0;
            host.userInteractionEnabled = NO;
            entry.host = host;
            [vc.view addSubview:host];

            // CD-3 + CD-7 prerequisite: bound view tree. daro-m fills these
            // during renderAd (sync, before listener.onAdLoadSuccess fires per
            // CommonAdNativeView.swift:185-187 — load-bearing for the click
            // bridge "wired before scrape" invariant).
            entry.titleLabel         = [UILabel new];
            entry.bodyLabel          = [UILabel new];
            entry.iconImageView      = [UIImageView new];
            entry.callToActionButton = [UIButton buttonWithType:UIButtonTypeSystem];
            entry.mediaContentView   = [UIView new];

            // CD-3: autoLoad=NO. Without this, addSubview(host) below would
            // auto-fire loadNativeAd via DaroObjCNativeView's didMoveToSuperview
            // (DaroObjCNativeView.swift:106-118) — racing past bindNativeViews.
            DaroObjCNativeView* nativeView = [[DaroObjCNativeView alloc]
                initWithUnitId:entry.adUnitId autoLoad:NO];
            nativeView.delegate = entry.delegate;
            nativeView.frame = host.bounds;
            entry.nativeView = nativeView;

            [host addSubview:nativeView];   // didMoveToSuperview fires;
                                            // addInternalNativeView attaches
                                            // DaroAdNativeView synchronously;
                                            // autoLoad=NO so no auto-load

            // Asset fix: AppLovin's MANativeAdViewBinder uses TAG-based lookup
            // (DaroAdNativeView.swift:64-73) — `renderNativeAdView(_, with:)`
            // traverses `maNativeAdView`'s subview tree and assigns ad assets
            // to views with matching tags. Loose UILabels held only by the
            // entry are tagged but never reached. Route them into the internal
            // tree first: nativeView.subviews.firstObject is DaroAdNativeView
            // (added by addInternalNativeView() via didMoveToSuperview); its
            // addSubview is overridden in CommonAdNativeView.swift:259 to
            // forward into maNativeAdView. After this, viewWithTag(...) on
            // maNativeAdView resolves these and the binder populates them
            // during render.
            UIView* internal_ = nativeView.subviews.firstObject;
            if (internal_) {
                [internal_ addSubview:entry.titleLabel];
                [internal_ addSubview:entry.bodyLabel];
                [internal_ addSubview:entry.iconImageView];
                [internal_ addSubview:entry.callToActionButton];
                [internal_ addSubview:entry.mediaContentView];
            }

            [nativeView bindNativeViewsWithIconImageView:entry.iconImageView
                                              titleLabel:entry.titleLabel
                                         advertiserLabel:nil
                                                bodyLabel:entry.bodyLabel
                                         mediaContentView:entry.mediaContentView
                                       callToActionButton:entry.callToActionButton];
            [nativeView loadNativeAd];
        });
    });
}

void DaroUnity_NativeAd_NotifyVisible(int handleId) {
    // v1 parity with Android signature — log only.
    DaroLogD(@"Native", @"notifyVisible handleId=%d", handleId);
}

void DaroUnity_NativeAd_NotifyHidden(int handleId) {
    DaroLogD(@"Native", @"notifyHidden handleId=%d", handleId);
}

void DaroUnity_NativeAd_NotifyClicked(int handleId) {
    NSNumber* key = @(handleId);
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry || entry.destroyed) return;
        UIButton* btn = entry.callToActionButton;
        if (!btn) return;   // race: load not yet completed — silent no-op
                            // (sketch §5.2; Android Finding C iOS parity but
                            // without the bare-event fallback — phantom clicks
                            // without an underlying ad to launch surface as
                            // "nothing happened" rather than a dud event)
        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;
            // CD-7: bridge Unity Button click → MAX click chain via the hidden
            // CTA UIButton's UIControl event dispatch. AppLovin's
            // renderNativeAdView:withAd: wires UIControlEventTouchUpInside on
            // the supplied callToActionButton during render (synchronous,
            // before onAdLoadSuccess per CommonAdNativeView.swift:185-187).
            // sendActionsForControlEvents: bypasses hit-testing, so the host's
            // hitTest:→nil block doesn't break this path.
            [btn sendActionsForControlEvents:UIControlEventTouchUpInside];
        });
    });
}

void DaroUnity_NativeAd_Destroy(int handleId) {
    NSNumber* key = @(handleId);
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry) return;   // unknown handleId — silent no-op (idempotent;
                              // C# side has _disposed gate)

        entry.destroyed = YES;   // Layer-1 armed — in-flight delegate
                                  // callbacks short-circuit
        DaroObjCNativeView*    nativeView = entry.nativeView;
        DaroUnityNativeAdHost* host       = entry.host;

        dispatch_async(dispatch_get_main_queue(), ^{
            [nativeView removeFromSuperview];
            [host       removeFromSuperview];
            // ARC: nilling entry below releases nativeView →
            //   DaroAdNativeView (internal) deinit → DaroAdNativeLoader deinit
            //   (cancels pending continuation, but does NOT call
            //   MANativeAdLoader.destroyAd: on _loadedAd — see
            //   docs/dev/native-ad-ios/source-verification-notes.md V2.
            //   Per-dispose MAAd leak risk is quantified at smoke time, not
            //   worked around in v1).
        });

        s_nativeAds[key] = nil;   // ARC drops entry + delegate + view tree
    });
}

}  // extern "C"
