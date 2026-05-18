//
//  DaroUnityBannerAd.mm
//  Banner ObjC++ shim — wraps DaroObjCBannerView (DaroMObjCBridge module)
//  for Unity. Parallel to Android's DaroUnityBannerAd.kt; full design in
//  See docs/features/native-bridge.md (Banner overlay / iOS).
//
//  Lifecycle (sketch §"Overlay Lifecycle State Machine"):
//
//    CreateBanner        → entry slot only, no view yet
//    LoadBanner(size)    → construct DaroObjCBannerView + addSubview hidden=YES + loadAd
//    ShowBanner          → addSubview (if needed) + hidden=NO   ← always set hidden=NO
//    HideBanner          → removeFromSuperview                   ← preserves hidden=YES
//    DestroyBanner       → removeFromSuperview + nil entry (ARC releases all)
//
//  Threading: dictionary mutations on s_adQueue (serial); UIView ops dispatched
//  to dispatch_get_main_queue. DaroMObjCBridge guarantees delegate callbacks
//  on the main queue (sketch CD-7), so DaroDispatch from delegate methods
//  needs no further marshaling.
//
//  Auto-refresh: native CommonAdBannerView's AdRefreshCoordinator is driven
//  by didMoveToSuperview / isHidden setter — addSubview+hidden=NO resumes,
//  removeFromSuperview pauses. The shim does no explicit coordinator calls.
//

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <DaroMObjCBridge/DaroMObjCBridge.h>
#import <DaroMObjCBridge/DaroMObjCBridge-Swift.h>
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
@end

@implementation DaroUnityBannerEntry
@end

#pragma mark - Storage (defined here, declared extern in DaroUnityBridgeInternal.h)

NSMutableDictionary<NSString*, DaroUnityBannerEntry*>* s_banners = nil;

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

// adFormat:0 = DaroAdFormat.Banner. The protocol is 4 methods only — no
// adShown/adHidden/adDismissed exist on DaroObjCBannerViewDelegate; those
// are synthesized C#-side (OnAdShown by DaroBannerAd.Show, OnAdHidden by
// DaroIOSPlatform.HideBanner — sketch CD-6).
@interface DaroUnityBannerDelegate : NSObject <DaroObjCBannerViewDelegate>
@property (nonatomic, copy) NSString* adUnitId;
@end

@implementation DaroUnityBannerDelegate

- (void)bannerViewDidLoad:(DaroObjCBannerView*)bannerView
                   adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"Banner", @"didLoad adUnit='%@'", self.adUnitId);
    DaroDispatch(self.adUnitId,
        [NSString stringWithFormat:@"{\"event\":\"adLoaded\",\"adFormat\":0%@}",
            LatencyField(adInfo)]);
}

- (void)bannerView:(DaroObjCBannerView*)bannerView
  didFailWithError:(NSError*)error {
    // NSError code is fixed at -1 by DaroObjCBannerView (domain
    // com.daro.objcbridge.banner). C# DaroAdErrorCodeMapper.ToLoadErrorCode(-1)
    // resolves to DaroAdLoadErrorCode.Unspecified (sketch CD-8).
    DaroLogW(@"Banner", @"Load failed adUnit='%@' code=%ld msg='%@'",
        self.adUnitId, (long)error.code, error.localizedDescription);
    DaroDispatch(self.adUnitId,
        [NSString stringWithFormat:
            @"{\"event\":\"adFailedToLoad\",\"adFormat\":0,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
            (long)error.code, EscapeJson(error.localizedDescription)]);
}

- (void)bannerViewDidClick:(DaroObjCBannerView*)bannerView
                    adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"Banner", @"didClick adUnit='%@'", self.adUnitId);
    DaroDispatch(self.adUnitId,
        [NSString stringWithFormat:@"{\"event\":\"adClicked\",\"adFormat\":0%@}",
            LatencyField(adInfo)]);
}

- (void)bannerViewDidRecordImpression:(DaroObjCBannerView*)bannerView
                               adInfo:(DaroObjCAdInfo*)adInfo {
    DaroLogD(@"Banner", @"didRecordImpression adUnit='%@'", self.adUnitId);
    DaroDispatch(self.adUnitId,
        [NSString stringWithFormat:@"{\"event\":\"adImpression\",\"adFormat\":0%@}",
            LatencyField(adInfo)]);
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
        s_banners[unit] = nil;                            // Replace any prior entry (ARC release)
        DaroUnityBannerEntry* entry = [DaroUnityBannerEntry new];
        entry.positionOrdinal = 4;                        // BottomCenter default (matches Android)
        entry.sizeOrdinal     = 0;                        // Standard default; overwritten at LoadBanner
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

        DaroObjCBannerSize nativeSize = (sizeOrdinal == 1)
            ? DaroObjCBannerSizeMrec : DaroObjCBannerSizeBanner;

        // Snapshot prior state for main-queue teardown (re-load pattern).
        DaroObjCBannerView* oldView = entry.bannerView;

        // Construct adopter + view off the main queue. UIView init does no
        // window operations until addSubview; setRootViewController + loadAd
        // happen on main.
        DaroUnityBannerDelegate* delegate = [DaroUnityBannerDelegate new];
        delegate.adUnitId = unit;
        DaroObjCBannerView* view = [[DaroObjCBannerView alloc]
            initWithUnitId:unit bannerSize:nativeSize autoLoad:NO];
        view.delegate = delegate;
        entry.bannerView = view;
        entry.delegate   = delegate;

        dispatch_async(dispatch_get_main_queue(), ^{
            [oldView removeFromSuperview];                // No-op if nil

            UIViewController* vc = UnityGetGLViewController();
            if (!vc) {
                DaroLogW(@"Banner", @"Load no GLViewController adUnit='%@'", unit);
                return;
            }
            [view setRootViewController:vc];

            // DETACHED LOAD: do NOT addSubview here. Daro CommonAdBannerView's
            // loadAd() uses loader.refresh() which is hierarchy-agnostic for
            // fetch — the network request and onAdLoaded callback fire whether
            // the view is in a window or not. Impression-counting (MAX
            // viewability) requires a window, so by keeping the view detached
            // we ensure no impression / refresh-cycle starts until ShowBanner
            // attaches the view. This matches DaroFlutterSDK's pattern (load =
            // ad object preparation, mount = display + impression).
            //
            // Frame computation is also deferred to ShowBanner because
            // safeAreaInsets requires the parent view, which is only relevant
            // at attach time.
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
        int posOrd = entry.positionOrdinal;
        int sizeOrd = entry.sizeOrdinal;
        dispatch_async(dispatch_get_main_queue(), ^{
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
            view.frame = BannerFrameForPosition(posOrd, BannerSizeForOrdinal(sizeOrd), parentView);
            [parentView addSubview:view];
            DaroLogD(@"Banner", @"Show.attached adUnit='%@' frame=%@",
                unit, NSStringFromCGRect(view.frame));
            // No hidden flag toggle — addSubview alone makes the view visible,
            // and Daro CommonAdBannerView's didMoveToSuperview callback will
            // wake AdRefreshCoordinator + signal MAX viewability → impression.
        });
    });
}

void DaroUnity_HideBanner(const char* adUnitId) {
    if (!adUnitId) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitId];
    DaroLogD(@"Banner", @"Hide adUnit='%@'", unit);
    dispatch_async(s_adQueue, ^{
        DaroObjCBannerView* view = s_banners[unit].bannerView;
        if (!view) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            // removeFromSuperview triggers didMoveToSuperview(nil) on the
            // native CommonAdBannerView, which pauses AdRefreshCoordinator.
            // No native "adHidden" callback fires — DaroIOSPlatform.HideBanner
            // synthesizes OnAdHidden in C# (sketch CD-6).
            [view removeFromSuperview];
            DaroLogD(@"Banner", @"Hide.detached adUnit='%@'", unit);
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
        // ordinal is consumed at next LoadBanner; if hidden, applied at
        // next ShowBanner via the LoadBanner path doesn't re-run, so fall
        // through to the cached frame on next visible attach.
        if (!view || !view.superview) return;
        int sizeOrd = entry.sizeOrdinal;
        dispatch_async(dispatch_get_main_queue(), ^{
            UIViewController* vc = UnityGetGLViewController();
            if (!vc) return;
            view.frame = BannerFrameForPosition(positionOrdinal,
                                                BannerSizeForOrdinal(sizeOrd),
                                                vc.view);
        });
    });
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
