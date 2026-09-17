//
//  DaroUnityNativeAd.mm
//  Native ad ObjC++ shim — wraps DaroObjCNativeView (DaroObjCBridge module)
//  for Unity. Parallel to Android's DaroUnityNativeAd.kt.

//
//  Lifecycle:
//    Create        → entry slot only, no view yet
//    Load          → construct hidden host UIView (touch gate off) +
//                    DaroObjCNativeView (autoLoad=NO) + bound view tree +
//                    bindNativeViews + loadNativeAd
//    NotifyVisible → request presentation once loaded and valid CTA geometry exists
//    NotifyHidden  → hide the entire native subtree and close its touch gate
//    NotifyClicked → diagnostic log only; real clicks use UIKit overlay touch
//    Destroy       → Layer-1 destroyed=YES + view removeFromSuperview + dict nil
//
//  Multi-instance: handleId-keyed s_nativeAds NSDictionary;
//  same adUnitId across N handles yields N independent entries.
//
//  Threading: dictionary mutations on s_adQueue (serial); UIView ops and native
//  ad delegate handling run on dispatch_get_main_queue. Native callback origin
//  can vary by MAX adapter callback; delegate entrypoints re-enter main before
//  touching state/UI, and the emit helper preserves main-queue Unity delivery.
//
//  Asset transport: dedicated callback channel
//    void(*)(int handleId, const char* eventJson, const uint8_t* iconPng, int iconLen)
//  carries PNG bytes on adLoaded; NULL/0 on every other event.
//

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <QuartzCore/QuartzCore.h>
#import <AppLovinSDK/AppLovinSDK.h>
#import <GoogleMobileAds/GoogleMobileAds.h>
#import <DaroObjCBridge/DaroObjCBridge.h>
#import <DaroObjCBridge/DaroObjCBridge-Swift.h>
#import "DaroUnityBridgeInternal.h"
#import "DaroUnityLog.h"

#pragma mark - Forward declarations

@class DaroUnityNativeAdDelegate;
@class DaroUnityNativeAdHost;
@class DaroUnityInvisibleCTAButton;

#pragma mark - Callback channel

typedef void (*DaroNativeAdCallbackFn)(int handleId,
                                       const char* eventJson,
                                       const uint8_t* iconPng,
                                       int iconLen);
static DaroNativeAdCallbackFn s_nativeAdCallback = NULL;

static void DaroUnityNativeAdEmitCallback(int handleId,
                                          NSString* eventJson,
                                          const uint8_t* iconPng,
                                          int iconLen) {
    DaroNativeAdCallbackFn callback = s_nativeAdCallback;
    if (!callback || !eventJson) return;

    BOOL hasIcon = iconPng != NULL && iconLen > 0;
    if ([NSThread isMainThread]) {
        callback(handleId,
                 [eventJson UTF8String],
                 hasIcon ? iconPng : NULL,
                 hasIcon ? iconLen : 0);
        return;
    }

    NSString* eventCopy = [NSString stringWithString:eventJson];
    NSData*   iconCopy  = hasIcon ? [NSData dataWithBytes:iconPng length:(NSUInteger)iconLen] : nil;
    dispatch_async(dispatch_get_main_queue(), ^{
        const uint8_t* copiedIcon = iconCopy ? (const uint8_t*)iconCopy.bytes : NULL;
        int            copiedLen  = iconCopy ? (int)iconCopy.length : 0;
        callback(handleId, [eventCopy UTF8String], copiedIcon, copiedLen);
    });
}

#pragma mark - Entry, delegate, host

// Per-instance entry — strong refs survive ARC drop until s_nativeAds[id] = nil.
@interface DaroUnityNativeAdEntry : NSObject
@property (nonatomic, copy)   NSString*                                  adUnitId;
@property (nonatomic, copy) NSString* assetTypes;
@property (nonatomic, assign) int                                        handleId;
// Load writes this on main, while destroy paths snapshot it on s_adQueue
// before hopping to main for UIKit teardown.
@property (atomic, strong, nullable) DaroObjCNativeView*                 nativeView;
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

// Layer-1 guard — atomic for cross-queue write/read safety. Set on
// destroy; checked at top of every delegate method, every extern C body
// after entry lookup, every dispatch_async closure, and inside the icon
// scrape recursion.
@property (atomic, assign) BOOL destroyed;

// Order-fix queue. MAX/daro-m adapter ordering has been observed both ways:
// impression/revenue can arrive before or after listener.onAdLoadSuccess.
// When impression beats adLoaded, pendingImpression queues it until
// scrapeAndDeliver emits adLoaded; when it arrives after adLoaded,
// loadedEmitted lets us emit immediately. Queue depth stays 0/1 because
// impressions are 1:1 with successful renders.
@property (atomic, assign)              BOOL              loadedEmitted;
@property (nonatomic, strong, nullable) DaroObjCAdInfo*   pendingImpression;

// Opt-in AdChoices area is independent of the CTA rectangle and gate.
@property (nonatomic, strong, nullable) NSNumber* adChoicesPosition;
@property (nonatomic, assign) CGRect adAreaPixels;
@property (nonatomic, assign) CGSize adAreaScreen;
@property (nonatomic, assign) BOOL hasAdArea;
@property (nonatomic, assign) BOOL adAreaVisible;

// Presentation and interaction are independent. All accesses are on main.
// Keep Unity pixels so re-show/reload converts against the current orientation.
@property (nonatomic, assign) BOOL visibleRequested;
@property (nonatomic, assign) BOOL readyForPresentation;
@property (nonatomic, assign) BOOL hasCtaRect;
@property (nonatomic, assign) CGRect ctaRectPixels;
@property (nonatomic, assign) BOOL ctaTouchEnabled;
@property (nonatomic, assign) BOOL ctaInteractive;
#if DARO_DEV || DEBUG
@property (nonatomic, assign) NSTimeInterval lastCtaOverlayEchoTime;
#endif
@end

@implementation DaroUnityNativeAdEntry
@end

static void DaroUnityNativeAdApplyAdChoicesGeometry(DaroUnityNativeAdEntry* entry);

static BOOL DaroUnityNativeAdRedispatchToMainIfNeeded(DaroUnityNativeAdEntry* entry,
                                                      DaroObjCNativeView* view,
                                                      dispatch_block_t block) {
    if ([NSThread isMainThread]) return NO;

    DaroUnityNativeAdEntry* entrySnapshot = entry;
    DaroObjCNativeView*     viewSnapshot  = view;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (entrySnapshot.destroyed) return;
        if (entrySnapshot.nativeView != viewSnapshot) return;
        block();
    });
    return YES;
}

// The host starts hidden. While displayed it uses alpha=1 + clearColor so
// native CTA gestures can receive real touches. Hiding the parent also hides
// vendor-owned auxiliary UI; disabling interaction alone cannot do that.
@interface DaroUnityNativeAdHost : UIView {
    BOOL _touchEnabled;
}
@property (nonatomic, assign) BOOL adChoicesEnabled;
@property (nonatomic, weak) UIButton* ctaButton;
- (void)setOverlayTouchEnabled:(BOOL)enabled;
@end

// Use vendor-owned disclosure views; retain their real hit-test and popup behavior.
static UIView* DaroUnityFindAdChoices(UIView* view) {
    if ([view isKindOfClass:[GADAdChoicesView class]]) return view;
    for (UIView* child in view.subviews) {
        UIView* choices = DaroUnityFindAdChoices(child);
        if (choices) return choices;
    }
    // A mediated Google view can live below MAX while MAX's own options
    // container is empty. Prefer the nested vendor disclosure in that case.
    if ([view isKindOfClass:[MANativeAdView class]])
        return ((MANativeAdView*)view).optionsContentView;
    return nil;
}

// Default Google disclosure is not a GADAdChoicesView (the custom-view API
// requires account access). Preserve its real UIKit target without depending
// on Google's private attribution-view class name. Asset touches still use
// the separate CTA gate below.
static UIView* DaroUnityHitGoogleAuxiliaryView(UIView* view, CGPoint point, UIEvent* event) {
    if (view.hidden || view.alpha <= 0.01 || !view.userInteractionEnabled ||
        ![view pointInside:point withEvent:event]) return nil;
    if ([view isKindOfClass:[GADNativeAdView class]]) {
        GADNativeAdView* google = (GADNativeAdView*)view;
        UIView* hit = [google hitTest:point withEvent:event];
        if (!hit || hit == google) return nil;
        NSArray* assets = @[google.headlineView ?: NSNull.null, google.bodyView ?: NSNull.null,
            google.advertiserView ?: NSNull.null, google.iconView ?: NSNull.null,
            google.mediaView ?: NSNull.null, google.imageView ?: NSNull.null,
            google.callToActionView ?: NSNull.null, google.priceView ?: NSNull.null,
            google.storeView ?: NSNull.null, google.starRatingView ?: NSNull.null];
        for (id asset in assets) {
            // A disabled asset can make UIKit return its wrapping container.
            // That container is still an asset region, not disclosure UI.
            if (asset != NSNull.null && (hit == asset || [hit isDescendantOfView:asset] ||
                                        [asset isDescendantOfView:hit])) return nil;
        }
        return hit;
    }
    for (UIView* child in view.subviews) {
        UIView* hit = DaroUnityHitGoogleAuxiliaryView(child, [child convertPoint:point fromView:view], event);
        if (hit) return hit;
    }
    return nil;
}

@implementation DaroUnityNativeAdHost

- (instancetype)initWithFrame:(CGRect)frame {
    self = [super initWithFrame:frame];
    if (self) {
        _touchEnabled              = NO;
        self.userInteractionEnabled = NO;
        self.alpha                  = 1.0;
        self.hidden                 = YES;
        self.backgroundColor        = [UIColor clearColor];
        self.opaque                 = NO;
    }
    return self;
}

- (void)setOverlayTouchEnabled:(BOOL)enabled {
    _touchEnabled              = enabled;
    self.userInteractionEnabled = enabled || self.adChoicesEnabled;
}

- (UIView*)hitTest:(CGPoint)point withEvent:(UIEvent*)event {
    if (self.hidden || !CGRectContainsPoint(self.bounds, point)) return nil;
    if (self.adChoicesEnabled) {
        UIView* choices = DaroUnityFindAdChoices(self);
        if (choices && !choices.hidden) {
            UIView* hit = [choices hitTest:[choices convertPoint:point fromView:self] withEvent:event];
            if (hit) return hit;
        }
        UIView* googleDisclosure = DaroUnityHitGoogleAuxiliaryView(self, point, event);
        if (googleDisclosure) return googleDisclosure;
        if (!_touchEnabled || !self.ctaButton) return nil;
        CGPoint ctaPoint = [self.ctaButton convertPoint:point fromView:self];
        if (![self.ctaButton pointInside:ctaPoint withEvent:event]) return nil;
        // AdMob disables interaction on the asset button and handles the
        // real touch on its enclosing native ad view. Keep the CTA-only
        // gate, but let UIKit select the current vendor's touch receiver.
        return [super hitTest:point withEvent:event];
    }
    if (!_touchEnabled) return nil;
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
              nativeView:(DaroObjCNativeView*)view
                  adInfo:(DaroObjCAdInfo*)info
              assetTypes:(NSString*)assetTypes
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

static void DaroUnityNativeAdUpdatePresentation(DaroUnityNativeAdEntry* entry);

#pragma mark - Delegate adopter implementation

@implementation DaroUnityNativeAdDelegate

- (void)nativeView:(DaroObjCNativeView*)view didLoadAssetTypes:(NSArray<NSString*>*)assetTypes {
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;
    if (DaroUnityNativeAdRedispatchToMainIfNeeded(entry, view, ^{
        [self nativeView:view didLoadAssetTypes:assetTypes];
    })) return;
    if (entry.nativeView != view) return;
    entry.assetTypes = [assetTypes componentsJoinedByString:@","];
}

- (void)nativeViewDidLoad:(DaroObjCNativeView*)view
                   adInfo:(DaroObjCAdInfo*)adInfo {
    // Retain the entry before dispatching asynchronous work:
    // snapshot weak self.entry to a strong local at entry of every callback.
    // Without this, dict-slot release inside DaroUnity_NativeAd_Destroy can
    // drop the last strong ref to entry, turning subsequent self.entry into
    // a nil weak read — and `nil.destroyed` returns NO, defeating the guard.
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;
    if (DaroUnityNativeAdRedispatchToMainIfNeeded(entry, view, ^{
        [self nativeViewDidLoad:view adInfo:adInfo];
    })) return;
    if (entry.nativeView != view) return;

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

    [self scrapeAndDeliver:entry nativeView:view adInfo:adInfo assetTypes:entry.assetTypes ?: @"" attempt:0];
}

- (void)nativeView:(DaroObjCNativeView*)view
  didFailWithError:(NSError*)error {
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;
    if (DaroUnityNativeAdRedispatchToMainIfNeeded(entry, view, ^{
        [self nativeView:view didFailWithError:error];
    })) return;
    if (entry.nativeView != view) return;
    entry.readyForPresentation = NO;
    DaroUnityNativeAdUpdatePresentation(entry);
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adFailedToLoad\",\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
        (long)error.code, EscapeJson(error.localizedDescription)];
    DaroUnityNativeAdEmitCallback(entry.handleId, json, NULL, 0);
}

- (void)nativeViewDidClick:(DaroObjCNativeView*)view
                    adInfo:(DaroObjCAdInfo*)adInfo {
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;
    if (DaroUnityNativeAdRedispatchToMainIfNeeded(entry, view, ^{
        [self nativeViewDidClick:view adInfo:adInfo];
    })) return;
    if (entry.nativeView != view) return;
    // Click truth signal — production-useful click attribution log.
    // The iOS overlay is a single touch consumer, so this delegate is the
    // only path that confirms a real UITouch reached AppLovin's GR.
    // Asymmetric vs `NotifyClicked` log below: `nativeViewDidClick` =
    // success signal; `NotifyClicked` on iOS = overlay-miss diagnostic
    // (Unity Button received the touch instead).
    DaroLogW(@"Native", @"nativeViewDidClick callback h=%d", entry.handleId);
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adClicked\"%@}", AdInfoFields(adInfo)];
    DaroUnityNativeAdEmitCallback(entry.handleId, json, NULL, 0);
}

- (void)nativeViewDidRecordImpression:(DaroObjCNativeView*)view
                               adInfo:(DaroObjCAdInfo*)adInfo {
    DaroUnityNativeAdEntry* entry = self.entry;
    if (!entry || entry.destroyed) return;
    if (DaroUnityNativeAdRedispatchToMainIfNeeded(entry, view, ^{
        [self nativeViewDidRecordImpression:view adInfo:adInfo];
    })) return;
    if (entry.nativeView != view) return;

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
        NSString* json = [NSString stringWithFormat:
            @"{\"event\":\"adImpression\"%@}", AdInfoFields(adInfo)];
        DaroUnityNativeAdEmitCallback(entry.handleId, json, NULL, 0);
    } else {
        entry.pendingImpression = adInfo;
    }
}

// icon scrape with 5×200ms polling fallback. iOS MAX adapters mostly
// resolve icon synchronously (image is non-nil at delegate fire time), but
// URL-based adapters (rare) may not — parity with Android Glide polling.
- (void)scrapeAndDeliver:(DaroUnityNativeAdEntry*)entry
              nativeView:(DaroObjCNativeView*)view
                  adInfo:(DaroObjCAdInfo*)info
              assetTypes:(NSString*)assetTypes
                 attempt:(int)attempt {
    if (entry.destroyed) return;
    if (entry.nativeView != view) return;

    // Ad networks register clicks on either the CTA or an ancestor view.
    // Gesture counts include UIKit's own recognizers and cannot determine
    // whether an ad supports clicks. Re-evaluate attachment for every fill
    // so a previous detached fill cannot keep later ads disabled.
    if (attempt == 0) {
        UIButton* button = entry.callToActionButton;
        entry.ctaInteractive = button != nil && view != nil
            && [button isDescendantOfView:view];
        if (!entry.ctaInteractive && entry.host) {
            [entry.host setOverlayTouchEnabled:NO];
        }
    }

    UIImage* image = entry.iconImageView.image;
    if (!image && attempt < kIconPollMaxAttempts) {
        __weak DaroUnityNativeAdDelegate* weakSelf = self;
        dispatch_after(
            dispatch_time(DISPATCH_TIME_NOW,
                          (int64_t)(kIconPollIntervalSec * NSEC_PER_SEC)),
            dispatch_get_main_queue(), ^{
                [weakSelf scrapeAndDeliver:entry nativeView:view adInfo:info assetTypes:assetTypes attempt:attempt + 1];
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

    // isCtaInteractive flag — false signals a detached CTA. C# reads via
    // DaroJsonHelpers.GetJsonBool with default true (back-compat for
    // Android/Editor sinks that don't emit this field).
    NSString* json = [NSString stringWithFormat:
        @"{\"event\":\"adLoaded\",\"title\":\"%@\",\"body\":\"%@\","
        @"\"callToAction\":\"%@\",\"assetTypes\":\"%@\",\"isCtaInteractive\":%@%@}",
        EscapeJson(title), EscapeJson(body), EscapeJson(cta), EscapeJson(assetTypes),
        entry.ctaInteractive ? @"true" : @"false",
        AdInfoFields(info)];

    // png lifetime: scrapeAndDeliver is main-only, so the emit helper calls
    // synchronously here and C# Marshal.Copy's png.bytes to a managed byte[]
    // before returning. Do NOT dispatch this icon-bearing call asynchronously
    // without copying first; the autorelease pool can drain and dangle bytes.
    //
    // ObjC++ note: NSData.bytes returns `const void*`, which ObjC++
    // (.mm) refuses to implicitly convert to `const uint8_t*` (unlike
    // ObjC .m). Explicit cast required.
    const uint8_t* iconBytes = png ? (const uint8_t*)png.bytes : nullptr;
    int            iconLen   = png ? (int)png.length : 0;
    DaroUnityNativeAdEmitCallback(entry.handleId, json, iconBytes, iconLen);

    // Order-fix: mark adLoaded emitted; flush any impression that arrived
    // during the scrape polling window (would otherwise have beaten
    // adLoaded on the wire — Daro iOS fires impression on revenue paid,
    // ~5ms after onAdLoadSuccess, while polling defers up to 1s).
    entry.loadedEmitted = YES;
    entry.readyForPresentation = YES;
    DaroUnityNativeAdUpdatePresentation(entry);
    DaroObjCAdInfo* pending = entry.pendingImpression;
    entry.pendingImpression = nil;
    if (pending) {
        NSString* impressionJson = [NSString stringWithFormat:
            @"{\"event\":\"adImpression\"%@}", AdInfoFields(pending)];
        DaroUnityNativeAdEmitCallback(entry.handleId, impressionJson, NULL, 0);
    }
}

@end

#pragma mark - AdChoices geometry

static CGRect DaroUnityProjectNativeRect(CGRect pixels, CGSize source, UIView* root) {
    CGFloat sx = root.bounds.size.width / MAX(source.width, 1);
    CGFloat sy = root.bounds.size.height / MAX(source.height, 1);
    return CGRectMake(pixels.origin.x * sx,
        root.bounds.size.height - CGRectGetMaxY(pixels) * sy,
        pixels.size.width * sx, pixels.size.height * sy);
}

static void DaroUnityNativeAdApplyAdChoicesGeometry(DaroUnityNativeAdEntry* entry) {
    if (!entry.adChoicesPosition || !entry.host) return;
    UIView* root = UnityGetGLViewController().view;
    BOOL visible = !entry.destroyed && entry.readyForPresentation && entry.hasAdArea &&
        entry.adAreaVisible && entry.visibleRequested && root.window != nil;
    CGRect rect = DaroUnityProjectNativeRect(entry.adAreaPixels, entry.adAreaScreen, root);
    visible = visible && rect.size.width > 0 && rect.size.height > 0 &&
        CGRectIntersectsRect(root.bounds, rect);
    entry.host.hidden = !visible;
    if (!visible) { [entry.host setOverlayTouchEnabled:NO]; return; }
    entry.host.frame = rect;
    entry.nativeView.frame = entry.host.bounds;
    // Native layout uses leading/trailing. Opt-in Unity corners are physical.
    entry.nativeView.semanticContentAttribute = UISemanticContentAttributeForceLeftToRight;
    BOOL touch = entry.hasCtaRect && entry.ctaTouchEnabled && entry.ctaInteractive;
    CGRect cta = DaroUnityProjectNativeRect(entry.ctaRectPixels, entry.adAreaScreen, root);
    cta.origin.x -= rect.origin.x;
    cta.origin.y -= rect.origin.y;
    entry.callToActionButton.frame = cta;
    [entry.host setOverlayTouchEnabled:touch];
    [entry.nativeView layoutIfNeeded];
}

#pragma mark - CTA overlay apply helpers

// Apply presentation as a whole; no geometry update may reopen a hidden ad.
// Retaining the attached tree preserves the existing native refresh policy.
static void DaroUnityNativeAdUpdatePresentation(DaroUnityNativeAdEntry* entry) {
    if (entry.adChoicesPosition) {
        DaroUnityNativeAdApplyAdChoicesGeometry(entry);
        return;
    }
    DaroUnityNativeAdHost* host = entry.host;
    if (!host) return;

    UIViewController* vc = UnityGetGLViewController();
    BOOL visible = !entry.destroyed && entry.visibleRequested
        && entry.readyForPresentation && entry.hasCtaRect && vc.view.window != nil;
    if (visible) {
        CGFloat scale = vc.view.window.screen.scale;
        CGRect pixels = entry.ctaRectPixels;
        CGRect rect = CGRectMake(pixels.origin.x / scale,
            vc.view.bounds.size.height - CGRectGetMaxY(pixels) / scale,
            pixels.size.width / scale, pixels.size.height / scale);
        visible = CGRectIntersectsRect(vc.view.bounds, rect);
        host.frame = rect;
        entry.nativeView.frame = host.bounds;
        entry.callToActionButton.frame = host.bounds;
    }
    host.hidden = !visible;
    [host setOverlayTouchEnabled:visible && entry.ctaTouchEnabled && entry.ctaInteractive];
}

#if DARO_DEV || DEBUG
static NSString* DaroUnityNativeAdHitName(DaroUnityNativeAdEntry* entry, UIView* hit) {
    if (!hit) return @"nil";
    if (hit == entry.callToActionButton || [hit isDescendantOfView:entry.callToActionButton]) {
        return @"cta";
    }
    if (hit == entry.nativeView || [hit isDescendantOfView:entry.nativeView]) {
        return @"native";
    }
    if (hit == entry.host || [hit isDescendantOfView:entry.host]) {
        return @"host";
    }

    UIViewController* vc = UnityGetGLViewController();
    if (vc && hit == vc.view) return @"UnityView";
    return NSStringFromClass(hit.class);
}

static void DaroUnityNativeAdLogCtaOverlayEcho(DaroUnityNativeAdEntry* entry,
                                                CGRect unityRectPx,
                                                CGRect uiRect,
                                                CGFloat scale,
                                                CGFloat unityScreenH_px,
                                                BOOL requestedTouch,
                                                BOOL effectiveTouch) {
    UIViewController* vc = UnityGetGLViewController();
    DaroUnityNativeAdHost* host = entry.host;
    if (!vc || !host) return;

    NSTimeInterval now = CACurrentMediaTime();
    if (entry.lastCtaOverlayEchoTime > 0.0 &&
        now - entry.lastCtaOverlayEchoTime < 0.25) {
        return;
    }
    entry.lastCtaOverlayEchoTime = now;

    CGPoint center = CGPointMake(CGRectGetMidX(uiRect), CGRectGetMidY(uiRect));
    UIView* hit = [vc.view hitTest:center withEvent:nil];
    BOOL inHost = hit && (hit == host || [hit isDescendantOfView:host]);
    NSUInteger subviewIndex = host.superview
        ? [host.superview.subviews indexOfObject:host]
        : NSNotFound;

    DaroLogD(@"Native",
             @"CtaOverlayEcho h=%d unity=(%.1f,%.1f,%.1f,%.1f) ui=%@ scale=%.2f screenHpx=%.1f requested=%d effective=%d interactive=%d attached=%d subviewIndex=%ld hit=%@ inHost=%d point=%@",
             entry.handleId,
             unityRectPx.origin.x,
             unityRectPx.origin.y,
             unityRectPx.size.width,
             unityRectPx.size.height,
             NSStringFromCGRect(uiRect),
             scale,
             unityScreenH_px,
             (int)requestedTouch,
             (int)effectiveTouch,
             (int)entry.ctaInteractive,
             host.window ? 1 : 0,
             (long)(subviewIndex == NSNotFound ? -1 : subviewIndex),
             DaroUnityNativeAdHitName(entry, hit),
             (int)inHost,
             NSStringFromCGPoint(center));
}
#endif

#pragma mark - extern C surface (matches [DllImport] in DaroIOSNativeAdHandle.cs)

extern "C" {

void DaroUnity_NativeAd_SetCallback(DaroNativeAdCallbackFn callback) {
    s_nativeAdCallback = callback;
}

void DaroUnity_NativeAd_Create(int handleId, const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    NSNumber* key  = @(handleId);

    dispatch_async(s_adQueue, ^{
        // Replace any existing entry — duplicate-construction-replaces.
        s_nativeAds[key] = nil;

        DaroUnityNativeAdEntry* entry = [DaroUnityNativeAdEntry new];
        entry.adUnitId = unit;
        entry.handleId = handleId;
        entry.visibleRequested = NO;

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
            entry.assetTypes = @"";
            entry.loadedEmitted     = NO;
            entry.pendingImpression = nil;
            entry.readyForPresentation = NO;

            // Explicit per-load reset. ObjC zero-default 의존 금지 — first
            // Load of a fresh entry needs YES. scrapeAndDeliver checks
            // attachment again after each successful render.
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

            // Hidden before attachment: network UI may render during load.
            DaroUnityNativeAdHost* host = [[DaroUnityNativeAdHost alloc]
                initWithFrame:CGRectMake(0, 0, hostWidth, hostHeight)];
            entry.host = host;
            host.adChoicesEnabled = entry.adChoicesPosition != nil;
            [vc.view addSubview:host];

            // Bind the view tree before loading. daro-m fills these
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
            host.ctaButton = entry.callToActionButton;
            if (host.adChoicesEnabled) {
                entry.titleLabel.alpha = 0;
                entry.bodyLabel.alpha = 0;
                entry.iconImageView.alpha = 0;
                entry.mediaContentView.alpha = 0;
            }

            // autoLoad=NO. Without this, addSubview(host) below would
            // auto-fire loadNativeAd via DaroObjCNativeView's didMoveToSuperview
            // (DaroObjCNativeView.swift:106-118) — racing past bindNativeViews.
            DaroObjCNativeView* nativeView = entry.adChoicesPosition
                ? [[DaroObjCNativeView alloc] initWithUnitId:entry.adUnitId autoLoad:NO
                    preferredAdChoicesPosition:(DaroAdChoicesPosition)entry.adChoicesPosition.integerValue]
                : [[DaroObjCNativeView alloc] initWithUnitId:entry.adUnitId autoLoad:NO];
            nativeView.delegate = entry.delegate;
            // ILRD is a billing datapoint and does not mutate entry state, so
            // keep it handle-routed even if the originating view was reloaded.
            int handleId = entry.handleId;
            nativeView.onPaidEvent = ^(DaroObjCAdRevenue* revenue) {
                // Revenue callbacks are the remaining path that can
                // originate off-main, so they intentionally rely on
                // DaroUnityNativeAdEmitCallback's main-queue marshal.
                //
                // 미디에이션 귀속은 싣지 않는다. onPaidEvent 는 DaroObjCAdRevenue
                // 하나만 받는데(SDK 의 DaroAdRevenue 자체가 그렇다), 네이티브는
                // didPayRevenue 가 renderAd 중에 동기로 터져 adInfo 를 나르는
                // 콜백들보다 앞선다 — 위 nativeViewDidRecordImpression 주석에
                // 실측으로 적혀 있다. 기억해 둔 값을 실으면 직전 사이클의
                // 네트워크를 새 노출에 적게 된다.
                NSString* json = [NSString stringWithFormat:
                    @"{\"event\":\"adRevenuePaid\"%@}",
                    RevenueFields(revenue.valueMicros, revenue.currencyCode, revenue.precision)];
                DaroUnityNativeAdEmitCallback(handleId, json, NULL, 0);
            };
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

            DaroUnityNativeAdUpdatePresentation(entry);
        });
    });
}

static void DaroUnityNativeAdSetVisibility(int handleId, BOOL visible) {
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[@(handleId)];
        if (!entry || entry.destroyed) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;
            entry.visibleRequested = visible;
            DaroUnityNativeAdUpdatePresentation(entry);
        });
    });
}

void DaroUnity_NativeAd_ConfigureAdChoices(int handleId, int position) {
    if (position < 0 || position > 3) return;
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[@(handleId)];
        if (!entry || entry.destroyed) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (!entry.destroyed) entry.adChoicesPosition = @(position);
        });
    });
}

void DaroUnity_NativeAd_SetAdChoicesScreenRect(int handleId, float x, float y,
    float w, float h, bool visible, int screenWidth, int screenHeight) {
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[@(handleId)];
        if (!entry || entry.destroyed) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;
            entry.adAreaPixels = CGRectMake(x, y, w, h);
            entry.adAreaScreen = CGSizeMake(screenWidth, screenHeight);
            entry.hasAdArea = w > 0 && h > 0 && screenWidth > 0 && screenHeight > 0;
            entry.adAreaVisible = visible;
            DaroUnityNativeAdApplyAdChoicesGeometry(entry);
        });
    });
}

void DaroUnity_NativeAd_ClearAdChoicesScreenRect(int handleId) {
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[@(handleId)];
        if (!entry || entry.destroyed) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            entry.hasAdArea = NO;
            DaroUnityNativeAdApplyAdChoicesGeometry(entry);
        });
    });
}

void DaroUnity_NativeAd_NotifyVisible(int handleId) {
    DaroUnityNativeAdSetVisibility(handleId, YES);
}

void DaroUnity_NativeAd_NotifyHidden(int handleId) {
    DaroUnityNativeAdSetVisibility(handleId, NO);
}

void DaroUnity_NativeAd_NotifyClicked(int handleId) {
    // Do not use
    // `[btn sendActionsForControlEvents:UIControlEventTouchUpInside]`
    // — a synthetic UIControl-event dispatch intended to bridge Unity's
    // Button.onClick → AppLovin's click chain.
    // AppLovin wires clicks via `UITapGestureRecognizer` (not UIControl
    // target/action), so sendActions does not fire the recognizer. The
    // click path runs through the iOS overlay (Geometry-sync UIView
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

// Unity pixel coordinates (bottom-left origin). Cache independently of Load
// and visibility; the presentation helper converts against the current screen.
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

            BOOL valid = isfinite(x) && isfinite(y) && isfinite(w) && isfinite(h)
                && w > 0 && h > 0 && isfinite(x + w) && isfinite(y + h);
            entry.hasCtaRect = valid;
            entry.ctaRectPixels = CGRectMake(x, y, w, h);
            entry.ctaTouchEnabled = valid && touchEnabled;
            DaroUnityNativeAdUpdatePresentation(entry);
#if DARO_DEV || DEBUG
            UIViewController* vc = UnityGetGLViewController();
            CGFloat scale = vc.view.window.screen.scale;
            DaroUnityNativeAdLogCtaOverlayEcho(entry, entry.ctaRectPixels,
                entry.host.frame, scale, vc.view.bounds.size.height * scale,
                touchEnabled, entry.host.userInteractionEnabled);
#endif
        });
    });
}

// Invalidate binding geometry and hide the subtree. Keep the visibility
// request: binding the same ad again supplies new geometry without OnEnable.
void DaroUnity_NativeAd_ClearCtaScreenRect(int handleId) {
    NSNumber* key = @(handleId);
    dispatch_async(s_adQueue, ^{
        DaroUnityNativeAdEntry* entry = s_nativeAds[key];
        if (!entry || entry.destroyed) return;

        dispatch_async(dispatch_get_main_queue(), ^{
            if (entry.destroyed) return;

            entry.hasCtaRect = NO;
            entry.ctaTouchEnabled = NO;
            DaroUnityNativeAdUpdatePresentation(entry);
            DaroLogD(@"Native", @"ClearCtaScreenRect h=%d (host=%@ pending=cleared)",
                     handleId, entry.host ? @"present" : @"nil");
        });
    });
}

void DaroUnity_NativeAd_Destroy(int handleId) {
    NSNumber* key = @(handleId);
    // Disposal invariant: destroyed=YES
    // must be observable to delegate callbacks BEFORE this function returns
    // to C#. With dispatch_async the block runs after the return, leaving a
    // window where a delegate fires on main with destroyed=NO (race B in
    // the disposal invariant). dispatch_sync forces the flag-set
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
            //   MANativeAdLoader.destroyAd: on _loadedAd.
            //   Per-dispose MAAd leak risk is quantified at smoke time, not
            //   worked around in v1).
        });

        s_nativeAds[key] = nil;   // A2: dict ref release AFTER destroyed=YES.
                                  // ARC drops entry + delegate + view tree
                                  // once the captured `nativeView`/`host`
                                  // strong refs above also release.
    });
}

// Runtime teardown cleanup. Called by
// DaroUnity_DestroyAll (DaroUnityBridge.mm) on app-quit / Unity-runtime-teardown.
// A2 invariant: set entry.destroyed=YES for every live entry BEFORE clearing
// the dict, so any in-flight delegate callback (which holds a strong-local
// retained snapshot of entry) reads YES
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
