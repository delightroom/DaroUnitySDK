//
//  DaroUnityBannerAd.mm
//  Banner ObjC++ shim — wraps DaroObjCBannerView (DaroObjCBridge module)
//  for Unity. Parallel to Android's DaroUnityBannerAd.kt; full design in
//  See docs/features/native-bridge.md (Banner overlay / iOS).
//
//  Lifecycle (sketch §"Overlay Lifecycle State Machine"):
//
//    CreateBanner        → entry slot only, no view yet
//    LoadBanner(size)    → construct DaroObjCBannerView + addSubview visible + loadAd
//    ShowBanner          → addSubview if hidden/detached
//    HideBanner          → removeFromSuperview
//    DestroyBanner       → removeFromSuperview + nil entry (ARC releases all)
//
//  Threading: dictionary mutations on s_adQueue (serial); UIView ops dispatched
//  to dispatch_get_main_queue. DaroObjCBridge guarantees delegate callbacks
//  on the main queue (sketch CD-7), so DaroDispatch from delegate methods
//  needs no further marshaling.
//
//  Auto-refresh: native CommonAdBannerView's AdRefreshCoordinator is driven
//  by didMoveToSuperview / isHidden setter — addSubview resumes,
//  removeFromSuperview pauses. The shim does no explicit coordinator calls.
//

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <DaroObjCBridge/DaroObjCBridge.h>
#import <DaroObjCBridge/DaroObjCBridge-Swift.h>
#import "DaroUnityBridgeInternal.h"
#import "DaroUnityLog.h"

#pragma mark - DaroUnityBannerEntry

// Strong refs to keep the banner view + delegate alive (delegate is `weak` on
// DaroObjCBannerView). Position/size ordinals stored so SetBannerPosition
// can recompute the frame without re-querying the view's instance state.
@interface DaroUnityBannerEntry : NSObject
@property (nonatomic, strong, nullable) DaroObjCBannerView* bannerView;
@property (nonatomic, strong, nullable) id<DaroObjCBannerViewDelegate> delegate;
@property (nonatomic, assign) int positionOrdinal;   // 0..5 = DaroBannerPosition
@property (nonatomic, assign) int sizeOrdinal;       // 0=Standard, 1=Mrec
@property (nonatomic, assign) BOOL visible;          // Load/Show requested + not yet Hidden — gates GetScreenRect
@property (nonatomic, assign) NSInteger generation;  // increments per Load; drops stale async main work/callbacks
@end

@implementation DaroUnityBannerEntry
@end

#pragma mark - Storage (defined here, declared extern in DaroUnityBridgeInternal.h)

NSMutableDictionary<NSString*, DaroUnityBannerEntry*>* s_banners = nil;
static NSInteger s_nextBannerGeneration = 0;

static NSInteger NextBannerGeneration(void) {
    // Called only from s_adQueue.
    s_nextBannerGeneration += 1;
    return s_nextBannerGeneration;
}

#pragma mark - Current-view guards

// Caller contract: never call from s_adQueue. Delegate / paid-event callbacks
// are native/main-queue driven, so dispatch_sync is safe here.
static BOOL BannerIsCurrent(NSString* unit,
                            DaroObjCBannerView* view,
                            BOOL requireVisible) {
    if (!unit || !view) return NO;
    __block BOOL current = NO;
    dispatch_sync(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        current = (entry && entry.bannerView == view &&
                   (!requireVisible || entry.visible));
    });
    return current;
}

static void DaroUnityWireBannerRevenue(DaroObjCBannerView* view,
                                       NSString* unit) {
    if (!view || !unit) return;

    __weak DaroObjCBannerView* weakView = view;
    view.onPaidEvent = ^(DaroObjCAdRevenue* revenue) {
        DaroObjCBannerView* strongView = weakView;
        if (!strongView || !BannerIsCurrent(unit, strongView, YES)) return;
        DaroDispatch(unit, [NSString stringWithFormat:
            @"{\"event\":\"adRevenuePaid\",\"adFormat\":0%@}",
            RevenueFields(revenue.value, revenue.currencyCode, revenue.precision)]);
    };
}

#pragma mark - Geometry helpers

// Match DaroObjCBannerSize source: .banner = 0 → 320×50, .mrec = 1 → 300×250.
static CGSize BannerSizeForOrdinal(int sizeOrdinal) {
    return (sizeOrdinal == 1) ? CGSizeMake(300, 250) : CGSizeMake(320, 50);
}

// 6-anchor frame computation, safe-area-aware (sketch CD-2).
//   posOrdinal:  0=TopLeft 1=TopCenter 2=TopRight
//                3=BottomLeft 4=BottomCenter 5=BottomRight
// parentView is UnityGetGLViewController().view; coords are in its bounds space.
static CGRect BannerFrameForPosition(int posOrdinal, CGSize bannerSize, UIView* parentView) {
    UIEdgeInsets insets = parentView.safeAreaInsets;
    CGFloat pW = parentView.bounds.size.width;
    CGFloat pH = parentView.bounds.size.height;
    CGFloat bW = bannerSize.width;
    CGFloat bH = bannerSize.height;

    CGFloat x;
    switch (posOrdinal) {
        case 0: case 3: x = insets.left;             break;  // Left
        case 2: case 5: x = pW - bW - insets.right;  break;  // Right
        case 1: case 4:
        default:        x = (pW - bW) / 2.0f;        break;  // Center
    }
    CGFloat y = (posOrdinal <= 2) ? insets.top : (pH - bH - insets.bottom);
    return CGRectMake(x, y, bW, bH);
}

#pragma mark - Delegate adopter

// adFormat:0 = DaroAdFormat.Banner. The native banner delegate has no
// adShown/adHidden/adDismissed callbacks. OnAdShown is synthesized by the
// C# DaroBannerAd Load/Show state; adHidden is emitted by this shim after
// native detach completes and routed through DaroIOSPlatform.
@interface DaroUnityBannerDelegate : NSObject <DaroObjCBannerViewDelegate>
@property (nonatomic, copy) NSString* adUnitId;
@end

@implementation DaroUnityBannerDelegate

- (void)bannerViewDidLoad:(DaroObjCBannerView*)bannerView
                   adInfo:(DaroObjCAdInfo*)adInfo {
    if (!BannerIsCurrent(self.adUnitId, bannerView, NO)) return;
    DaroLogD(@"Banner", @"didLoad adUnit='%@'", self.adUnitId);
    DaroDispatch(self.adUnitId,
        @"{\"event\":\"adLoaded\",\"adFormat\":0}");
}

- (void)bannerView:(DaroObjCBannerView*)bannerView
  didFailWithError:(NSError*)error {
    // NSError code is fixed at -1 by DaroObjCBannerView (domain
    // com.daro.objcbridge.banner). C# DaroAdErrorCodeMapper.ToLoadErrorCode(-1)
    // resolves to DaroAdLoadErrorCode.Unspecified (sketch CD-8).
    if (!BannerIsCurrent(self.adUnitId, bannerView, NO)) return;
    DaroLogW(@"Banner", @"Load failed adUnit='%@' code=%ld msg='%@'",
        self.adUnitId, (long)error.code, error.localizedDescription);
    NSString* unit = self.adUnitId;
    dispatch_sync(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        if (!entry || entry.bannerView != bannerView) return;
        entry.visible = NO;
        entry.bannerView = nil;
        entry.delegate = nil;
    });
    [bannerView removeFromSuperview];
    DaroDispatch(self.adUnitId,
        [NSString stringWithFormat:
            @"{\"event\":\"adFailedToLoad\",\"adFormat\":0,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
            (long)error.code, EscapeJson(error.localizedDescription)]);
}

- (void)bannerViewDidClick:(DaroObjCBannerView*)bannerView
                    adInfo:(DaroObjCAdInfo*)adInfo {
    if (!BannerIsCurrent(self.adUnitId, bannerView, YES)) return;
    DaroLogD(@"Banner", @"didClick adUnit='%@'", self.adUnitId);
    DaroDispatch(self.adUnitId,
        @"{\"event\":\"adClicked\",\"adFormat\":0}");
}

- (void)bannerViewDidRecordImpression:(DaroObjCBannerView*)bannerView
                               adInfo:(DaroObjCAdInfo*)adInfo {
    if (!BannerIsCurrent(self.adUnitId, bannerView, YES)) return;
    DaroLogD(@"Banner", @"didRecordImpression adUnit='%@'", self.adUnitId);
    DaroDispatch(self.adUnitId,
        @"{\"event\":\"adImpression\",\"adFormat\":0}");
}

@end

#pragma mark - extern C surface (matches [DllImport] in DaroIOSPlatform.cs)

extern "C" {

// Reserve the dictionary slot for adUnitId. The view is constructed lazily
// at LoadBanner because DaroObjCBannerView requires bannerSize at init time
// (sketch §"Option A design" — Create/Load split).
void DaroUnity_CreateBanner(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroObjCBannerView* oldView = s_banners[unit].bannerView;
        if (oldView) {
            dispatch_async(dispatch_get_main_queue(), ^{
                [oldView removeFromSuperview];
            });
        }
        s_banners[unit] = nil;                            // Replace any prior entry (ARC release)
        DaroUnityBannerEntry* entry = [DaroUnityBannerEntry new];
        entry.positionOrdinal = 4;                        // BottomCenter default (matches Android)
        entry.sizeOrdinal     = 0;                        // Standard default; overwritten at LoadBanner
        entry.generation      = NextBannerGeneration();
        s_banners[unit] = entry;
    });
}

void DaroUnity_LoadBanner(const char* adUnitId, int sizeOrdinal) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"Banner", @"Load adUnit='%@' size=%d", unit, sizeOrdinal);
    dispatch_async(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        if (!entry) return;
        entry.sizeOrdinal = sizeOrdinal;
        entry.visible = YES;
        entry.generation = NextBannerGeneration();
        NSInteger generation = entry.generation;

        // Snapshot prior state for main-queue teardown (re-load pattern).
        DaroObjCBannerView* oldView = entry.bannerView;
        entry.bannerView = nil;
        entry.delegate   = nil;

        dispatch_async(dispatch_get_main_queue(), ^{
            [oldView removeFromSuperview];                // No-op if nil

            UIViewController* vc = UnityGetGLViewController();
            if (!vc) {
                DaroLogW(@"Banner", @"Load no GLViewController adUnit='%@'", unit);
                __block BOOL shouldFail = NO;
                dispatch_sync(s_adQueue, ^{
                    DaroUnityBannerEntry* latest = s_banners[unit];
                    if (latest && latest.generation == generation) {
                        latest.visible = NO;
                        shouldFail = YES;
                    }
                });
                if (shouldFail) {
                    DaroDispatch(unit,
                        @"{\"event\":\"adFailedToLoad\",\"adFormat\":0,\"errorCode\":-1,\"errorMessage\":\"Unity GLViewController unavailable\"}");
                }
                return;
            }

            DaroObjCBannerSize nativeSize = (sizeOrdinal == 1)
                ? DaroObjCBannerSizeMrec : DaroObjCBannerSizeBanner;
            DaroUnityBannerDelegate* delegate = [DaroUnityBannerDelegate new];
            delegate.adUnitId = unit;
            DaroObjCBannerView* view = [[DaroObjCBannerView alloc]
                initWithUnitId:unit bannerSize:nativeSize autoLoad:NO];
            view.delegate = delegate;
            DaroUnityWireBannerRevenue(view, unit);
            [view setRootViewController:vc];

            __block BOOL current = NO;
            __block BOOL shouldAttach = NO;
            __block int latestPosOrd = 4;
            __block int latestSizeOrd = sizeOrdinal;
            dispatch_sync(s_adQueue, ^{
                DaroUnityBannerEntry* latest = s_banners[unit];
                if (latest && latest.generation == generation) {
                    latest.bannerView = view;
                    latest.delegate = delegate;
                    current = YES;
                    shouldAttach = latest.visible;
                    latestPosOrd = latest.positionOrdinal;
                    latestSizeOrd = latest.sizeOrdinal;
                }
            });
            if (!current) return;

            if (shouldAttach) {
                UIView* parentView = vc.view;
                view.frame = BannerFrameForPosition(
                    latestPosOrd, BannerSizeForOrdinal(latestSizeOrd), parentView);
                [parentView addSubview:view];
                DaroLogD(@"Banner", @"Load.attached adUnit='%@' frame=%@",
                    unit, NSStringFromCGRect(view.frame));
            }
            [view loadAd];
        });
    });
}

void DaroUnity_ShowBanner(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"Banner", @"Show adUnit='%@'", unit);
    dispatch_async(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        if (!entry) return;
        DaroObjCBannerView* view = entry.bannerView;
        if (!view) return;
        entry.visible = YES;   // intent set synchronously on s_adQueue (gates GetScreenRect)
        dispatch_async(dispatch_get_main_queue(), ^{
            __block BOOL current = NO;
            __block int latestPosOrd = 4;
            __block int latestSizeOrd = 0;
            dispatch_sync(s_adQueue, ^{
                DaroUnityBannerEntry* latest = s_banners[unit];
                if (latest && latest.bannerView == view && latest.visible) {
                    current = YES;
                    latestPosOrd = latest.positionOrdinal;
                    latestSizeOrd = latest.sizeOrdinal;
                }
            });
            if (!current) return;
            if (view.superview) return;                   // Already shown — no-op
            UIViewController* vc = UnityGetGLViewController();
            if (!vc) {
                DaroLogW(@"Banner", @"Show no GLViewController adUnit='%@'", unit);
                return;
            }
            UIView* parentView = vc.view;
            // Compute frame at attach time so safeAreaInsets reflects current
            // device orientation (CD-3: initial orientation only, but Show may
            // happen after device has rotated since Load — safest to recompute).
            view.frame = BannerFrameForPosition(
                latestPosOrd, BannerSizeForOrdinal(latestSizeOrd), parentView);
            [parentView addSubview:view];
            DaroLogD(@"Banner", @"Show.attached adUnit='%@' frame=%@",
                unit, NSStringFromCGRect(view.frame));
            // No hidden flag toggle — addSubview alone makes the view visible.
        });
    });
}

void DaroUnity_HideBanner(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"Banner", @"Hide adUnit='%@'", unit);
    dispatch_async(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        if (!entry) return;
        entry.visible = NO;    // clears the footprint gate before the async detach
        DaroObjCBannerView* view = entry.bannerView;
        if (!view) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            // removeFromSuperview triggers didMoveToSuperview(nil) on the
            // native CommonAdBannerView, which pauses AdRefreshCoordinator.
            // No native "adHidden" callback exists; this shim emits a
            // synthetic JSON event after the detach has actually completed.
            [view removeFromSuperview];
            DaroLogD(@"Banner", @"Hide.detached adUnit='%@'", unit);
            __block BOOL shouldDispatchHidden = NO;
            dispatch_sync(s_adQueue, ^{
                DaroUnityBannerEntry* latest = s_banners[unit];
                shouldDispatchHidden = (latest && latest.bannerView == view && !latest.visible);
            });
            if (shouldDispatchHidden) {
                DaroDispatch(unit, @"{\"event\":\"adHidden\",\"adFormat\":0}");
            }
        });
    });
}

void DaroUnity_DestroyBanner(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"Banner", @"Destroy adUnit='%@'", unit);
    dispatch_async(s_adQueue, ^{
        DaroObjCBannerView* view = s_banners[unit].bannerView;
        if (view) {
            dispatch_async(dispatch_get_main_queue(), ^{
                [view removeFromSuperview];
            });
        }
        s_banners[unit] = nil;       // ARC releases view + delegate together
    });
}

void DaroUnity_SetBannerPosition(const char* adUnitId, int positionOrdinal) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    dispatch_async(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        if (!entry) return;
        entry.positionOrdinal = positionOrdinal;
        DaroObjCBannerView* view = entry.bannerView;
        // Apply only if currently in hierarchy. If not yet loaded, stored
        // ordinal is consumed at LoadBanner attach; if hidden, ShowBanner
        // recomputes from the stored ordinal.
        if (!view) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            __block BOOL current = NO;
            __block int latestPosOrd = positionOrdinal;
            __block int latestSizeOrd = 0;
            dispatch_sync(s_adQueue, ^{
                DaroUnityBannerEntry* latest = s_banners[unit];
                if (latest && latest.bannerView == view && latest.visible) {
                    current = YES;
                    latestPosOrd = latest.positionOrdinal;
                    latestSizeOrd = latest.sizeOrdinal;
                }
            });
            if (!current || !view.superview) return;
            UIViewController* vc = UnityGetGLViewController();
            if (!vc) return;
            view.frame = BannerFrameForPosition(latestPosOrd,
                                                BannerSizeForOrdinal(latestSizeOrd),
                                                vc.view);
        });
    });
}

// Banner footprint query (banner-footprint sprint). Returns 1 + the banner's
// on-screen rect in Unity screen px (bottom-left origin, Screen.safeArea
// convention); 0 if the banner is not attached / unknown. The view.frame is
// already safe-area-correct (BannerFrameForPosition computes it from
// parentView.safeAreaInsets), so consumers get the real footprint without
// guessing from Unity's Screen.safeArea.
//
// Unity's iOS scripting thread is the UIKit main thread, so this normally runs
// on main; the [NSThread isMainThread] guard hops to main for any off-main
// caller. s_banners is read under s_adQueue (mutated there).
int DaroUnity_GetBannerScreenRect(const char* adUnitId,
                                  float* outX, float* outY,
                                  float* outW, float* outH) {
    if (!adUnitId) return 0;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];

    __block DaroObjCBannerView* view = nil;
    __block BOOL visible = NO;
    dispatch_sync(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        view    = entry.bannerView;
        visible = entry.visible;
    });
    // `visible` is cleared synchronously by Hide on s_adQueue, so a query racing
    // the async detach returns 0 instead of the stale rect.
    if (!view || !visible) return 0;

    __block CGRect frame  = CGRectZero;
    __block BOOL attached = NO;
    __block CGFloat parentHpx = 0.0;
    __block CGFloat scale = 1.0;
    void (^readGeometry)(void) = ^{
        UIViewController* vc = UnityGetGLViewController();
        UIView* parent = vc ? vc.view : nil;
        if (!parent) {
            attached = NO;
            return;
        }
        attached = (view.superview != nil);
        frame    = view.frame;                 // parentView coords, points, top-left
        scale = parent.window.screen.scale;
        if (scale <= 0.0) scale = UIScreen.mainScreen.scale;
        parentHpx = parent.bounds.size.height * scale;
    };
    if ([NSThread isMainThread]) readGeometry();
    else dispatch_sync(dispatch_get_main_queue(), readGeometry);
    if (!attached || frame.size.width <= 0.0) return 0;   // attached but not laid out yet
    if (parentHpx <= 0.0) return 0;

    float wpx = (float)(frame.size.width  * scale);
    float hpx = (float)(frame.size.height * scale);
    float xpx = (float)(frame.origin.x * scale);
    float topPx = (float)(frame.origin.y * scale);
    float yBottomLeftPx = (float)(parentHpx - (topPx + hpx));

    if (outX) *outX = xpx;
    if (outY) *outY = yBottomLeftPx;
    if (outW) *outW = wpx;
    if (outH) *outH = hpx;
    DaroLogD(@"Banner", @"GetScreenRect adUnit='%@' px=(%.0f,%.0f,%.0f,%.0f)",
        unit, xpx, yBottomLeftPx, wpx, hpx);
    return 1;
}

// Sprint native-object-lifecycle-cleanup §DestroyAll hygiene path. Called by
// DaroUnity_DestroyAll (DaroUnityBridge.mm). Banner has no entry-level
// `destroyed` flag (D-iOS-banner-conditional in plan §2): banner delegates
// don't read entry state in a way that would race during teardown — view
// removal + dict ref release on s_adQueue is sufficient. If a future
// banner-delegate change introduces entry-level callback state that needs
// per-instance suppression, add a `destroyed` BOOL to DaroUnityBannerEntry
// and follow NativeAd's A2 pattern here.
//
// Caller contract: must NOT be invoked from s_adQueue context — would deadlock.
void DaroUnityBanner_DestroyAll(void) {
    dispatch_sync(s_adQueue, ^{
        NSUInteger entryCount = s_banners.count;
        if (entryCount == 0) {
            DaroLogD(@"Banner", @"DestroyAll noop (no entries)");
            return;
        }

        NSMutableArray<UIView*>* viewsToRemove = [NSMutableArray array];
        for (DaroUnityBannerEntry* entry in s_banners.allValues) {
            if (entry.bannerView) [viewsToRemove addObject:entry.bannerView];
        }
        [s_banners removeAllObjects];

        if (viewsToRemove.count > 0) {
            dispatch_async(dispatch_get_main_queue(), ^{
                for (UIView* v in viewsToRemove) {
                    [v removeFromSuperview];
                }
            });
        }

        DaroLogD(@"Banner", @"DestroyAll cleared %lu entries, %lu views",
                 (unsigned long)entryCount, (unsigned long)viewsToRemove.count);
    });
}

}  // extern "C"
