//
//  DaroUnityNativeAd.mm
//  Native ad ObjC++ shim — wraps DaroObjCNativeView (DaroMObjCBridge module)
//  for Unity. Parallel to Android's DaroUnityNativeAd.kt; full design in
//  See docs/features/native-bridge.md (Native ad / iOS).
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
@class DaroUnityInvisibleCTAButton;

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

// CTA overlay sync state.
// Pre-Load cache — `SetCtaScreenRect` 가 host 생성 전 도착하면 여기 보관,
// Load 의 main-queue 블록이 host/button 생성 후 replay. `ctaInteractive`
// 는 매 Load 시작 시 explicit YES 로 reset (ObjC zero-default 의존 금지);
// `scrapeAndDeliver` 의 GR survey 가 unsupported 발견 시에만 NO 로 내림.
// 모든 access 는 main queue 로 직렬화 — atomic property 는 type safety
// 외 race protection 목적 아님.
@property (atomic, assign) CGRect pendingCtaRect;
@property (atomic, assign) BOOL   pendingCtaTouchEnabled;
@property (atomic, assign) BOOL   hasPendingCta;
@property (atomic, assign) BOOL   ctaInteractive;
@end

@implementation DaroUnityNativeAdEntry
@end

// CD-4 SUPERSEDED: host gains a runtime `_touchEnabled` gate. Default NO
// preserves the original CD-4 intent (touch-blocking) for the Load →
// first-SetCtaScreenRect window. When the publisher's CTA wire pushes
// touchEnabled=YES, both the `hitTest:` override and `userInteractionEnabled`
// open together — UIKit's documented hit-test rule ignores views with
// alpha<0.01, so visual transparency switches from `alpha=0` to
// `alpha=1 + clearColor` for the host to stay touch-receivable.
//
// hitTest: behavior:
//   _touchEnabled=NO                                   → nil (gate closed)
//   _touchEnabled=YES + point outside bounds          → nil (defensive)
//   _touchEnabled=YES + point inside bounds           → [super hitTest:]
//
// All reads/writes of `_touchEnabled` happen on the main queue (hitTest:
// in UIKit touch pipeline + `setOverlayTouchEnabled:` invoked from
// shim-side dispatch_async(main_queue)). Same-queue → no atomic needed.
@interface DaroUnityNativeAdHost : UIView {
    BOOL _touchEnabled;
}
- (void)setOverlayTouchEnabled:(BOOL)enabled;
@end

@implementation DaroUnityNativeAdHost

- (instancetype)initWithFrame:(CGRect)frame {
    self = [super initWithFrame:frame];
    if (self) {
        _touchEnabled              = NO;
        self.userInteractionEnabled = NO;
        self.alpha                  = 1.0;
        self.backgroundColor        = [UIColor clearColor];
        self.opaque                 = NO;
    }
    return self;
}

- (void)setOverlayTouchEnabled:(BOOL)enabled {
    _touchEnabled              = enabled;
    self.userInteractionEnabled = enabled;
}

- (UIView*)hitTest:(CGPoint)point withEvent:(UIEvent*)event {
    if (!_touchEnabled) return nil;
    if (!CGRectContainsPoint(self.bounds, point)) return nil;
    return [super hitTest:point withEvent:event];
}

@end

// AppLovin's `renderNativeAdView:` populates the bound CTA button via
// `setTitle:`/`setAttributedTitle:`/`setImage:`/`setBackgroundImage:` during
// synchronous render (CommonAdNativeView.swift:185-187). Publisher renders
// the CTA visual in Unity uGUI; the iOS button must be visually empty but
// hit-testable so AppLovin's UITapGestureRecognizer can still recognize
// real touches. Subclass overrides each visual-content setter to no-op and
// records the intended title in `lastIntendedTitle` so the scrape path
// can still forward the CTA string to publishers.
//
// `UIButton.buttonWithType:UIButtonTypeCustom` (default) — system buttons
// apply tint/highlight effects that survive content-clearing tricks.
@interface DaroUnityInvisibleCTAButton : UIButton
@property (nonatomic, copy, nullable) NSString* lastIntendedTitle;
@end

@implementation DaroUnityInvisibleCTAButton

- (instancetype)initWithFrame:(CGRect)frame {
    self = [super initWithFrame:frame];
    if (self) {
        [super setBackgroundColor:[UIColor clearColor]];
        self.opaque                       = NO;
        self.adjustsImageWhenHighlighted  = NO;
        self.showsTouchWhenHighlighted    = NO;
    }
    return self;
}

- (void)setTitle:(NSString*)title forState:(UIControlState)state {
    // Record only the Normal-state title — that's what scrape forwards to publishers.
    if (state == UIControlStateNormal && title.length > 0) {
        self.lastIntendedTitle = title;
    }
    // No-op on super — visual layer stays empty.
    (void)title; (void)state;
}

- (void)setAttributedTitle:(NSAttributedString*)title forState:(UIControlState)state {
    if (state == UIControlStateNormal && title.string.length > 0) {
        self.lastIntendedTitle = title.string;
    }
    (void)title; (void)state;
}

- (void)setImage:(UIImage*)image forState:(UIControlState)state {
    (void)image; (void)state;
}

- (void)setBackgroundImage:(UIImage*)image forState:(UIControlState)state {
    (void)image; (void)state;
}

- (void)setBackgroundColor:(UIColor*)backgroundColor {
    // Lock to clearColor regardless of caller — AppLovin templates may try
    // to set tinted backgrounds.
    [super setBackgroundColor:[UIColor clearColor]];
}

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
    // A2 retention precondition (teardown-contract §iOS concurrency model):
    // snapshot weak self.entry to a strong local at entry of every callback.
    // Without this, dict-slot release inside DaroUnity_NativeAd_Destroy can
    // drop the last strong ref to entry, turning subsequent self.entry into
    // a nil weak read — and `nil.destroyed` returns NO, defeating the guard.
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;

    // Order-fix: reset loadedEmitted at the start of EVERY load delivery —
    // covers daro-internal refresh-driven loads (CommonAdNativeView's
    // coordinator.refreshHandler → loadAd) which bypass our
    // DaroUnity_NativeAd_Load entry point. DO NOT clear pendingImpression
    // here: MAX's didPayRevenue fires synchronously during renderAd
    // (CommonAdNativeView.swift:185), which runs BEFORE listener.onAdLoadSuccess
    // (line 187, which triggers this delegate). So pendingImpression at this
    // point was queued for THIS cycle and must survive the reset to flush
    // after adLoaded emits.
    entry.loadedEmitted = NO;

    [self scrapeAndDeliver:entry adInfo:adInfo attempt:0];
}

- (void)nativeView:(DaroObjCNativeView*)view
  didFailWithError:(NSError*)error {
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToLoad\",\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    if (s_nativeAdCallback) {
        s_nativeAdCallback(entry.handleId, [json UTF8String], NULL, 0);
    }
}

- (void)nativeViewDidClick:(DaroObjCNativeView*)view
                    adInfo:(DaroObjCAdInfo*)adInfo {
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;
    // Click truth signal — production-useful click attribution log.
    // The iOS overlay is a single touch consumer, so this delegate is the
    // only path that confirms a real UITouch reached AppLovin's GR.
    // Asymmetric vs `NotifyClicked` log below: `nativeViewDidClick` =
    // success signal; `NotifyClicked` on iOS = overlay-miss diagnostic
    // (Unity Button received the touch instead).
    DaroLogW(@"Native", @"nativeViewDidClick callback h=%d", entry.handleId);
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adClicked\"%@}", LatencyField(adInfo)];
    if (s_nativeAdCallback) {
        s_nativeAdCallback(entry.handleId, [json UTF8String], NULL, 0);
    }
}

- (void)nativeViewDidRecordImpression:(DaroObjCNativeView*)view
                               adInfo:(DaroObjCAdInfo*)adInfo {
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;

    // Order-fix v2: handle BOTH callback orderings. The original code
    // assumed didPayRevenue (→ this delegate) ALWAYS runs BEFORE
    // listener.onAdLoadSuccess (→ nativeViewDidLoad) per the documented
    // CommonAdNativeView.swift:185-187 sequence. On real iOS first-loads
    // the order is observed REVERSED — onAdLoadSuccess emits first, this
    // delegate fires afterwards, and the now-already-flushed pendingImpression
    // slot leaves the impression silently dropped.
    //
    //  loadedEmitted=NO  → flush hasn't happened yet (the documented
    //                       order). Queue; scrapeAndDeliver will flush
    //                       after adLoaded emits.
    //  loadedEmitted=YES → adLoaded already on the wire. Emit immediately.
    //
    // Each load lifecycle resets loadedEmitted=NO at the top of
    // nativeViewDidLoad (line ~260) and DaroUnity_NativeAd_Load (line ~520),
    // so the branch is stable across refresh cycles.
    DaroLogD(@"Native", @"didRecordImpression h=%d loadedEmitted=%@ — %@",
             entry.handleId,
             entry.loadedEmitted ? @"YES" : @"NO",
             entry.loadedEmitted ? @"emitting directly" : @"queueing for flush");

    if (entry.loadedEmitted) {
        if (s_nativeAdCallback) {
            NSString* json = [NSString stringWithFormat:
                @"{\"event\":\"adImpression\"%@}", LatencyField(adInfo)];
            s_nativeAdCallback(entry.handleId, [json UTF8String], NULL, 0);
        }
    } else {
        entry.pendingImpression = adInfo;
    }
}

// CD-6 icon scrape with 5×200ms polling fallback. iOS MAX adapters mostly
// resolve icon synchronously (image is non-nil at delegate fire time), but
// URL-based adapters (rare) may not — parity with Android Glide polling.
- (void)scrapeAndDeliver:(DaroUnityNativeAdEntry*)entry
                  adInfo:(DaroObjCAdInfo*)info
                 attempt:(int)attempt {
    if (entry.destroyed) return;

    // GR survey + click-disabled-load detection.
    // Render completed synchronously upstream (CommonAdNativeView.swift:185
    // renderAd before line 187 onAdLoadSuccess), so any UITapGestureRecognizer
    // AppLovin attached is observable here. Gate on attempt==0 — wiring is
    // render-time stable; repeated polling re-entry would otherwise log
    // multiple times per load.
    //
    // Unsupported predicate: missingHierarchy || btnGR==0 || parentGR>0 ||
    // grandpaGR>0. On unsupported, we DO NOT reject — didPayRevenue already
    // fired during render so MAX-side impression is billed; rejecting at our
    // boundary would create an accounting mismatch + retry loop. Instead we
    // flag entry.ctaInteractive=NO + force host touch off. onAdLoaded /
    // onAdImpression continue to flow with isCtaInteractive=false in JSON
    // so publisher can hide the prefab via Info.IsCtaInteractive.
    if (attempt == 0) {
        UIButton* btn = entry.callToActionButton;
        BOOL missingHierarchy = (btn == nil || btn.superview == nil
                                 || btn.superview.superview == nil);
        NSUInteger btnGR     = btn ? btn.gestureRecognizers.count : 0;
        NSUInteger parentGR  = btn.superview ? btn.superview.gestureRecognizers.count : 0;
        NSUInteger grandpaGR = btn.superview.superview
                                 ? btn.superview.superview.gestureRecognizers.count : 0;
        DaroLogW(@"Native",
                 @"GRwire h=%d btnGR=%lu parentGR=%lu grandpaGR=%lu missingHierarchy=%@ adUnit='%@'",
                 entry.handleId,
                 (unsigned long)btnGR, (unsigned long)parentGR,
                 (unsigned long)grandpaGR,
                 missingHierarchy ? @"YES" : @"NO",
                 entry.adUnitId);

        BOOL unsupported = missingHierarchy || (btnGR == 0)
                            || (parentGR > 0) || (grandpaGR > 0);
        if (unsupported) {
            DaroLogW(@"Native",
                     @"GRwire h=%d UNSUPPORTED — isCtaInteractive=false, overlay touch off",
                     entry.handleId);
            entry.ctaInteractive = NO;
            if (entry.host) [entry.host setOverlayTouchEnabled:NO];
        }
    }

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
    // Invisible CTA button's setTitle: is a no-op on the visual layer, so
    // titleLabel.text is nil. The intended CTA string is recorded in the
    // subclass's `lastIntendedTitle` ivar — read that for asset transport.
    NSString* cta = @"";
    if ([entry.callToActionButton isKindOfClass:[DaroUnityInvisibleCTAButton class]]) {
        cta = ((DaroUnityInvisibleCTAButton*)entry.callToActionButton).lastIntendedTitle ?: @"";
    }

    // Bug #2 debug: scrape result. If title/body/cta empty + image nil →
    // daro-m didn't populate our loose UILabels (likely needs them as
    // subviews of maNativeAdView for tag-based binder to find them).
    DaroLogD(@"Native", @"scrape h=%d title='%@' body='%@' cta='%@' image=%@ icon=%@",
             entry.handleId,
             title, body, cta,
             image ? [NSString stringWithFormat:@"%dx%d", (int)image.size.width, (int)image.size.height] : @"nil",
             png ? [NSString stringWithFormat:@"%dB", (int)png.length] : @"nil");

    // isCtaInteractive flag — false signals publisher that click chain is
    // broken for this fill (unsupported GR wiring). C# parser reads via
    // DaroJsonHelpers.GetJsonBool with default true (back-compat for
    // Android/Editor sinks that don't emit this field).
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adLoaded\"%@,\"title\":\"%@\",\"body\":\"%@\","
        @"\"callToAction\":\"%@\",\"isCtaInteractive\":%@}",
        LatencyField(info),
        EscapeJson(title), EscapeJson(body), EscapeJson(cta),
        entry.ctaInteractive ? @"true" : @"false"];

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

#pragma mark - CTA overlay apply helpers

// Raw geometry-apply path. Writes host / nativeView / button frames in the
// order parent → child + flips host touch gate. internal_ + maNativeAdView
// follow via Autolayout edge-anchors. No `entry.ctaInteractive` check —
// that's the Guarded wrapper's job. Direct callers (eg. force-off from
// non-positive-size guard) call Raw with effectiveTouch=NO explicitly.
//
// Main-queue only. Caller must dispatch.
static void DaroUnityNativeAdApplyCtaRectRaw(DaroUnityNativeAdEntry* entry,
                                              CGRect uiRect,
                                              BOOL   effectiveTouch) {
    DaroUnityNativeAdHost* host       = entry.host;
    DaroObjCNativeView*    nativeView = entry.nativeView;
    UIButton*              button     = entry.callToActionButton;
    if (!host) return;   // race: Load tore down between checks. silent.

    host.frame       = uiRect;
    nativeView.frame = host.bounds;
    button.frame     = host.bounds;
    [host setOverlayTouchEnabled:effectiveTouch];
}

// Guarded geometry-apply path. All apply paths route here except the
// defensive force-off branches. Locks effectiveTouch = requestedTouch
// AND ctaInteractive — so once GR survey flags a fill as unsupported
// (entry.ctaInteractive=NO), no subsequent SetCtaScreenRect(touchEnabled=YES)
// from C# can re-open the gate.
//
// Main-queue only. Caller must dispatch.
static void DaroUnityNativeAdApplyCtaRectGuarded(DaroUnityNativeAdEntry* entry,
                                                  CGRect uiRect,
                                                  BOOL   requestedTouch) {
    BOOL effectiveTouch = requestedTouch && entry.ctaInteractive;
    DaroUnityNativeAdApplyCtaRectRaw(entry, uiRect, effectiveTouch);
}

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

            // Explicit per-load reset. ObjC zero-default 의존 금지 — first
            // Load of a fresh entry needs YES, and refresh-driven loads need
            // YES re-arm (last GR survey may have left NO). GR survey in
            // scrapeAndDeliver flips to NO only on unsupported predicate.
            entry.ctaInteractive = YES;

            UIViewController* vc = UnityGetGLViewController();
            if (!vc) return;   // Unity not ready — silently bail (Banner parity)

            // Load reentry guard: tear down any previous host/nativeView
            // from a prior Load on the same entry. Without this, the old
            // host stays in vc.view.subviews — and with overlay touch
            // possibly enabled — intercepting taps for a stale ad.
            if (entry.host) {
                [entry.host setOverlayTouchEnabled:NO];
                [entry.host removeFromSuperview];
            }
            if (entry.nativeView) {
                [entry.nativeView removeFromSuperview];
            }

            // CD-4 SUPERSEDED: host is touch-blocking by default
            // (_touchEnabled=NO + userInteractionEnabled=NO set in init), with
            // visual transparency via clearColor + alpha=1 (alpha<0.01 would
            // disable hit-test per Apple's documented rule). Default-closed
            // gate stays closed until SetCtaScreenRect(touchEnabled=YES) opens it.
            DaroUnityNativeAdHost* host = [[DaroUnityNativeAdHost alloc]
                initWithFrame:CGRectMake(0, 0, hostWidth, hostHeight)];
            entry.host = host;
            [vc.view addSubview:host];

            // CD-3 + CD-7 prerequisite: bound view tree. daro-m fills these
            // during renderAd (sync, before listener.onAdLoadSuccess fires per
            // CommonAdNativeView.swift:185-187 — load-bearing for the click
            // bridge "wired before scrape" invariant).
            entry.titleLabel         = [UILabel new];
            entry.bodyLabel          = [UILabel new];
            entry.iconImageView      = [UIImageView new];
            // Invisible-content CTA button — AppLovin's setTitle: / setImage:
            // / etc. become no-ops on the visual layer; intended title
            // preserved in `lastIntendedTitle` for the scrape path.
            entry.callToActionButton = [[DaroUnityInvisibleCTAButton alloc] initWithFrame:CGRectZero];
            entry.mediaContentView   = [UIView new];

            // CD-3: autoLoad=NO. Without this, addSubview(host) below would
            // auto-fire loadNativeAd via DaroObjCNativeView's didMoveToSuperview
            // (DaroObjCNativeView.swift:106-118) — racing past bindNativeViews.
            DaroObjCNativeView* nativeView = [[DaroObjCNativeView alloc]
                initWithUnitId:entry.adUnitId autoLoad:NO];
            nativeView.delegate = entry.delegate;
            // ILRD: handle-routed (multi-instance) — unlike unit-routed formats,
            // native revenue must reach the exact handle, so we capture handleId.
            NSString* paidToken = DaroUnityPaidEventToken();
            if (paidToken) {
                int handleId = entry.handleId;
                [nativeView registerPluginWithIdentifier:paidToken
                    onPaidEvent:^(DaroObjCAdInfo* adInfo, NSDecimalNumber* value,
                                  NSString* currencyCode, NSInteger precisionType) {
                        if (!s_nativeAdCallback) return;
                        NSString* json = [NSString stringWithFormat:
                            @"{\"event\":\"adRevenuePaid\"%@%@}",
                            RevenueFields(value, currencyCode, precisionType), LatencyField(adInfo)];
                        s_nativeAdCallback(handleId, [json UTF8String], NULL, 0);
                    }];
            }
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

            // Replay any pre-Load cached CTA rect now that host/nativeView/
            // button exist. hasPendingCta is the single source of truth — Clear
            // pre-Load already set it to NO, so no replay in that path.
            if (entry.hasPendingCta) {
                CGRect cached      = entry.pendingCtaRect;
                BOOL   cachedTouch = entry.pendingCtaTouchEnabled;
                entry.hasPendingCta = NO;
                DaroUnityNativeAdApplyCtaRectGuarded(entry, cached, cachedTouch);
                DaroLogD(@"Native", @"Load h=%d replayed cached CTA rect=%@ touch=%d",
                         handleId, NSStringFromCGRect(cached), (int)cachedTouch);
            }
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
    // CD-7 SUPERSEDED. The original path here was
    // `[btn sendActionsForControlEvents:UIControlEventTouchUpInside]`
    // — a synthetic UIControl-event dispatch intended to bridge Unity's
    // Button.onClick → AppLovin's click chain. Device diagnosis confirmed
    // AppLovin wires click via `UITapGestureRecognizer` (not UIControl
    // target/action), so sendActions never fired the recognizer. The real
    // click path now runs through the iOS overlay (Geometry-sync UIView
    // catches the user's UITouch; AppLovin's GR recognizes it normally).
    //
    // This function is retained as ABI (C# `_handle.NotifyClicked()` still
    // calls it) and repurposed as a diagnostic ack. On iOS the overlay
    // single-consumes the touch — Unity Button.onClick should NOT fire on a
    // normal click. If this log line *does* fire, it indicates Unity GL
    // surface received the touch, i.e. overlay z-order / geometry / hit-test
    // failed (overlay-miss). Asymmetric vs `nativeViewDidClick callback`
    // log (truth signal for actual click reaching MAX).
    NSNumber* key = @(handleId);
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry || entry.destroyed) return;
        UIButton* btn = entry.callToActionButton;
        if (!btn) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;
            DaroLogW(@"Native",
                     @"NotifyClicked h=%d btnGR=%lu parentGR=%lu grandpaGR=%lu",
                     handleId,
                     (unsigned long)btn.gestureRecognizers.count,
                     (unsigned long)btn.superview.gestureRecognizers.count,
                     (unsigned long)btn.superview.superview.gestureRecognizers.count);
            // No sendActionsForControlEvents — real UITouch via overlay is
            // the click path. Phantom firing here would double-count clicks
            // on AppLovin's GR-driven attribution.
        });
    });
}

// CTA overlay geometry sync. C# DaroNativeCtaDriver.LateUpdate sends
// per-frame (dirty-checked) rect + composite touchEnabled. Conversion:
// Unity pixel space + bottom-left → UIKit point + top-left here. Pre-Load
// → cache, Load replays after loadNativeAd. All apply paths route through
// the Guarded helper so `entry.ctaInteractive=NO` (set by GR survey on
// unsupported fills) locks the touch gate closed.
void DaroUnity_NativeAd_SetCtaScreenRect(int   handleId,
                                          float x,
                                          float y,
                                          float w,
                                          float h,
                                          bool  touchEnabled) {
    NSNumber* key = @(handleId);
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry || entry.destroyed) return;

        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;

            UIViewController* vc = UnityGetGLViewController();
            if (!vc) return;   // Unity not yet attached — silent bail.

            CGFloat scale = vc.view.window.screen.scale;
            if (scale <= 0.0) scale = [UIScreen mainScreen].scale;
            CGFloat unityScreenH_px = vc.view.bounds.size.height * scale;

            CGFloat uiX = x / scale;
            CGFloat uiW = w / scale;
            CGFloat uiH = h / scale;
            CGFloat uiY = (unityScreenH_px - y - h) / scale;
            CGRect  uiRect = CGRectMake(uiX, uiY, uiW, uiH);

            if (uiW <= 0.0 || uiH <= 0.0) {
                // Defensive — C# clamps Mathf.Max(1, ...) at IconSize but the
                // computed screen rect can still go zero if Button is collapsed
                // mid-layout (e.g., LayoutGroup transient). Treat as clear.
                DaroLogW(@"Native",
                         @"SetCtaScreenRect h=%d non-positive size w=%.2f h=%.2f — clearing overlay",
                         handleId, uiW, uiH);
                if (entry.host) [entry.host setOverlayTouchEnabled:NO];
                entry.hasPendingCta = NO;
                return;
            }

            if (!entry.host) {
                // Pre-Load. Cache for Load's main-queue block to replay after
                // host / nativeView / button construction.
                entry.pendingCtaRect         = uiRect;
                entry.pendingCtaTouchEnabled = touchEnabled ? YES : NO;
                entry.hasPendingCta          = YES;
                DaroLogD(@"Native", @"SetCtaScreenRect h=%d cached pre-Load rect=%@ touch=%d",
                         handleId, NSStringFromCGRect(uiRect), (int)touchEnabled);
                return;
            }

            // Post-Load fast path. Guarded helper enforces ctaInteractive lock.
            DaroUnityNativeAdApplyCtaRectGuarded(entry, uiRect, touchEnabled ? YES : NO);
            DaroLogD(@"Native", @"SetCtaScreenRect h=%d applied rect=%@ touch=%d",
                     handleId, NSStringFromCGRect(uiRect), (int)touchEnabled);
        });
    });
}

// Counterpart — frame intact (preserves MAX viewability-frame stability
// across refresh cycles), touch off. Pre-Load: clear cache flag only.
// Post-Load: flip host touch gate.
void DaroUnity_NativeAd_ClearCtaScreenRect(int handleId) {
    NSNumber* key = @(handleId);
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry || entry.destroyed) return;

        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;

            entry.hasPendingCta = NO;

            if (entry.host) {
                [entry.host setOverlayTouchEnabled:NO];
            }
            DaroLogD(@"Native", @"ClearCtaScreenRect h=%d (host=%@ pending=cleared)",
                     handleId, entry.host ? @"present" : @"nil");
        });
    });
}

void DaroUnity_NativeAd_Destroy(int handleId) {
    NSNumber* key = @(handleId);
    // A2 invariant (teardown-contract §iOS concurrency model): destroyed=YES
    // must be observable to delegate callbacks BEFORE this function returns
    // to C#. With dispatch_async the block runs after the return, leaving a
    // window where a delegate fires on main with destroyed=NO (race B in
    // teardown-contract §A2 commentary). dispatch_sync forces the flag-set
    // to happen synchronously on s_adQueue.
    //
    // Caller contract: Destroy must NOT be called from s_adQueue context —
    // would deadlock. C# DllImport callers run on Unity main or worker
    // threads, never on s_adQueue.
    dispatch_sync(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry) return;   // unknown handleId — silent no-op (idempotent;
                              // C# side has _disposed gate)

        entry.destroyed = YES;   // A2: first observable teardown step.
                                  // ObjC `atomic` accessor provides seq-cst
                                  // cross-queue visibility; main-queue
                                  // delegates reading `entry.destroyed`
                                  // after this point see YES.
        DaroObjCNativeView*    nativeView = entry.nativeView;
        DaroUnityNativeAdHost* host       = entry.host;

        dispatch_async(dispatch_get_main_queue(), ^{
            [nativeView removeFromSuperview];
            [host       removeFromSuperview];
            // ARC: nilling entry below releases nativeView →
            //   DaroAdNativeView (internal) deinit → DaroAdNativeLoader deinit
            //   (cancels pending continuation, but does NOT call
            //   MANativeAdLoader.destroyAd: on _loadedAd — see
            //   docs/features/native-bridge.md Native ad / iOS notes.
            //   Per-dispose MAAd leak risk is quantified at smoke time, not
            //   worked around in v1).
        });

        s_nativeAds[key] = nil;   // A2: dict ref release AFTER destroyed=YES.
                                  // ARC drops entry + delegate + view tree
                                  // once the captured `nativeView`/`host`
                                  // strong refs above also release.
    });
}

// Sprint native-object-lifecycle-cleanup §DestroyAll hygiene path. Called by
// DaroUnity_DestroyAll (DaroUnityBridge.mm) on app-quit / Unity-runtime-teardown.
// A2 invariant: set entry.destroyed=YES for every live entry BEFORE clearing
// the dict, so any in-flight delegate callback (which holds a strong-local
// snapshot of entry per §iOS concurrency model retention contract) reads YES
// and bails before reaching DaroDispatch.
//
// Caller contract: must NOT be invoked from s_adQueue context — would deadlock.
void DaroUnityNativeAd_DestroyAll(void) {
    dispatch_sync(s_adQueue, ^{
        NSUInteger entryCount = s_nativeAds.count;
        if (entryCount == 0) {
            DaroLogD(@"Native", @"DestroyAll noop (no entries)");
            return;
        }

        NSMutableArray<UIView*>* viewsToRemove = [NSMutableArray array];
        for (DaroUnityNativeAdEntry* entry in s_nativeAds.allValues) {
            entry.destroyed = YES;   // A2: armed before dict ref release
            if (entry.nativeView) [viewsToRemove addObject:entry.nativeView];
            if (entry.host)       [viewsToRemove addObject:entry.host];
        }
        [s_nativeAds removeAllObjects];

        if (viewsToRemove.count > 0) {
            dispatch_async(dispatch_get_main_queue(), ^{
                for (UIView* v in viewsToRemove) {
                    [v removeFromSuperview];
                }
            });
        }

        DaroLogD(@"Native", @"DestroyAll cleared %lu entries, %lu views",
                 (unsigned long)entryCount, (unsigned long)viewsToRemove.count);
    });
}

}  // extern "C"
