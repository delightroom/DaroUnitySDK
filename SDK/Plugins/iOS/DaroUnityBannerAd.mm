//
//  DaroUnityBannerAd.mm
//  Banner ObjC++ shim — wraps DaroObjCBannerView (DaroObjCBridge module)
//  for Unity. Parallel to Android's DaroUnityBannerAd.kt.

//
//  Lifecycle:
//
//    CreateBanner        → entry slot only, no view yet
//    PreloadBanner(size) → construct DaroObjCBannerView(preload:YES) detached; the SDK
//                          requests at construction and holds one ad (TTL 10 min)
//    LoadBanner(size)    → preload view present: attach it (attach = consume, no loadAd)
//                          otherwise: construct DaroObjCBannerView + addSubview visible + loadAd
//    ShowBanner          → addSubview if hidden/detached
//    HideBanner          → removeFromSuperview
//    DestroyBanner       → removeFromSuperview + nil entry (ARC releases all)
//
//  Threading: dictionary mutations on s_adQueue (serial); UIView ops dispatched
//  to dispatch_get_main_queue. DaroObjCBridge guarantees delegate callbacks
//  on the main queue, so DaroDispatch from delegate methods
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

#pragma mark - Forward declarations

@class DaroUnityBannerDelegate;

#pragma mark - DaroUnityBannerEntry

// Strong refs to keep the banner view + delegate alive (delegate is `weak` on
// DaroObjCBannerView). Position/size ordinals stored so SetBannerPosition
// can recompute the frame without re-querying the view's instance state.
@interface DaroUnityBannerEntry : NSObject
@property (nonatomic, strong, nullable) DaroObjCBannerView* bannerView;
@property (nonatomic, strong, nullable) DaroUnityBannerDelegate* delegate;
@property (nonatomic, assign) int positionOrdinal;   // 0..5 = DaroBannerPosition
@property (nonatomic, assign) int sizeOrdinal;       // 0=Standard, 1=Mrec
@property (nonatomic, assign) BOOL visible;          // Load/Show requested + not yet Hidden — gates GetScreenRect
@property (nonatomic, assign) NSInteger generation;  // increments per Load; drops stale async main work/callbacks
// Preload. 받아 두었고 아직 LoadBanner 가 가져가지 않은 뷰. 계층 밖에 있다.
// 가져가면 bannerView / delegate 로 옮기고 여기는 비운다.
@property (nonatomic, strong, nullable) DaroObjCBannerView* preloadView;
@property (nonatomic, strong, nullable) DaroUnityBannerDelegate* preloadDelegate;
@property (nonatomic, assign) int preloadSizeOrdinal;
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

// preload 결과용. 받아 둔 뷰와 이미 LoadBanner 가 가져간 뷰 ( 로딩 중 연결 ) 를 모두 인정한다.
// Caller contract: BannerIsCurrent 와 같다 — s_adQueue 에서 부르지 않는다.
static BOOL BannerOwnsView(NSString* unit, DaroObjCBannerView* view) {
    if (!unit || !view) return NO;
    __block BOOL owns = NO;
    dispatch_sync(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        owns = (entry && (entry.preloadView == view || entry.bannerView == view));
    });
    return owns;
}

static void DaroUnityWireBannerRevenue(DaroObjCBannerView* view,
                                       NSString* unit) {
    if (!view || !unit) return;

    // 수익 이벤트에는 미디에이션 귀속을 싣지 않는다. `onPaidEvent` 는
    // DaroObjCAdRevenue 하나만 받고(SDK 의 DaroAdRevenue 자체가 그렇다), 배너 수익은
    // 로드/리프레시 버스트 안에서 터져 그 안의 콜백 순서가 보장되지 않는다. 델리게이트가
    // 기억해 둔 값을 대신 실으면 새 노출을 직전 낙찰 네트워크로 적을 수 있다.
    // 나머지 배너 이벤트는 각자 받은 adInfo 를 그대로 싣는다.
    __weak DaroObjCBannerView* weakView = view;
    view.onPaidEvent = ^(DaroObjCAdRevenue* revenue) {
        DaroObjCBannerView* strongView = weakView;
        if (!strongView || !BannerIsCurrent(unit, strongView, YES)) return;
        DaroDispatch(unit, [NSString stringWithFormat:
            @"{\"event\":\"adRevenuePaid\",\"adFormat\":0%@}",
            RevenueFields(revenue.valueMicros, revenue.currencyCode, revenue.precision)]);
    };
}

#pragma mark - Geometry helpers

// Match DaroObjCBannerSize source: .banner = 0 → 320×50, .mrec = 1 → 300×250.
static CGSize BannerSizeForOrdinal(int sizeOrdinal) {
    return (sizeOrdinal == 1) ? CGSizeMake(300, 250) : CGSizeMake(320, 50);
}

// 6-anchor frame computation, safe-area-aware.
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
@property (nonatomic, copy) NSString* adUnitKey;
// 마지막으로 받은 adInfo. Hide 가 만드는 합성 adHidden 이벤트에는 딸려오는 adInfo 가
// 없어서 이 자리 말고는 그 이벤트에 미디에이션 귀속을 실을 방법이 없다.
@property (nonatomic, strong, nullable) DaroObjCAdInfo* lastAdInfo;
@end

@implementation DaroUnityBannerDelegate

- (void)bannerViewDidLoad:(DaroObjCBannerView*)bannerView
                   adInfo:(DaroObjCAdInfo*)adInfo {
    if (!BannerIsCurrent(self.adUnitKey, bannerView, NO)) return;
    DaroLogD(@"Banner", @"didLoad adUnit='%@'", self.adUnitKey);
    self.lastAdInfo = adInfo;
    DaroDispatch(self.adUnitKey, [NSString stringWithFormat:
        @"{\"event\":\"adLoaded\",\"adFormat\":0%@}", AdInfoFields(adInfo)]);
}

- (void)bannerView:(DaroObjCBannerView*)bannerView
  didFailWithError:(NSError*)error {
    // NSError code is fixed at -1 by DaroObjCBannerView (domain
    // com.daro.objcbridge.banner). C# DaroAdErrorCodeMapper.ToLoadErrorCode(-1)
    // resolves to DaroAdLoadErrorCode.Unspecified.
    if (!BannerIsCurrent(self.adUnitKey, bannerView, NO)) return;
    DaroLogW(@"Banner", @"Load failed adUnit='%@' code=%ld msg='%@'",
        self.adUnitKey, (long)error.code, error.localizedDescription);
    NSString* unit = self.adUnitKey;
    dispatch_sync(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        if (!entry || entry.bannerView != bannerView) return;
        entry.visible = NO;
        entry.bannerView = nil;
        entry.delegate = nil;
    });
    [bannerView removeFromSuperview];
    DaroDispatch(self.adUnitKey,
        [NSString stringWithFormat:
            @"{\"event\":\"adFailedToLoad\",\"adFormat\":0,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
            (long)error.code, EscapeJson(error.localizedDescription)]);
}

// preload:YES 로 만든 뷰가 부착 전에 받아 둔 광고의 결과. 부착 뒤의 표시는 bannerViewDidLoad: 로 온다.
- (void)bannerViewDidPreload:(DaroObjCBannerView*)bannerView
                      adInfo:(DaroObjCAdInfo*)adInfo {
    if (!BannerOwnsView(self.adUnitKey, bannerView)) return;
    DaroLogD(@"Banner", @"didPreload adUnit='%@'", self.adUnitKey);
    DaroDispatch(self.adUnitKey, [NSString stringWithFormat:
        @"{\"event\":\"adPreloaded\",\"adFormat\":0%@}", AdInfoFields(adInfo)]);
}

// 뷰는 버리지 않는다 — LoadBanner 가 붙이면 SDK 가 일반 로드로 폴백한다. NSError code 는
// didFailWithError: 와 같이 DaroObjCBannerView 가 -1 로 고정한다.
- (void)bannerView:(DaroObjCBannerView*)bannerView
didFailToPreloadWithError:(NSError*)error {
    if (!BannerOwnsView(self.adUnitKey, bannerView)) return;
    DaroLogW(@"Banner", @"Preload failed adUnit='%@' code=%ld msg='%@'",
        self.adUnitKey, (long)error.code, error.localizedDescription);
    DaroDispatch(self.adUnitKey,
        [NSString stringWithFormat:
            @"{\"event\":\"adFailedToPreload\",\"adFormat\":0,\"errorCode\":%ld,\"errorMessage\":\"%@\"}",
            (long)error.code, EscapeJson(error.localizedDescription)]);
}

- (void)bannerViewDidClick:(DaroObjCBannerView*)bannerView
                    adInfo:(DaroObjCAdInfo*)adInfo {
    if (!BannerIsCurrent(self.adUnitKey, bannerView, YES)) return;
    DaroLogD(@"Banner", @"didClick adUnit='%@'", self.adUnitKey);
    self.lastAdInfo = adInfo;
    DaroDispatch(self.adUnitKey, [NSString stringWithFormat:
        @"{\"event\":\"adClicked\",\"adFormat\":0%@}", AdInfoFields(adInfo)]);
}

- (void)bannerViewDidRecordImpression:(DaroObjCBannerView*)bannerView
                               adInfo:(DaroObjCAdInfo*)adInfo {
    if (!BannerIsCurrent(self.adUnitKey, bannerView, YES)) return;
    DaroLogD(@"Banner", @"didRecordImpression adUnit='%@'", self.adUnitKey);
    self.lastAdInfo = adInfo;
    DaroDispatch(self.adUnitKey, [NSString stringWithFormat:
        @"{\"event\":\"adImpression\",\"adFormat\":0%@}", AdInfoFields(adInfo)]);
}

@end

#pragma mark - extern C surface (matches [DllImport] in DaroIOSPlatform.cs)

extern "C" {

// Reserve the dictionary slot for adUnitKey. The view is constructed lazily
// at LoadBanner because DaroObjCBannerView requires bannerSize at init time (Create/Load split).
void DaroUnity_CreateBanner(const char* adUnitKey) {
    if (!adUnitKey) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];
    dispatch_async(s_adQueue, ^{
        DaroObjCBannerView* oldView = s_banners[unit].bannerView;
        DaroObjCBannerView* oldPreload = s_banners[unit].preloadView;
        if (oldView || oldPreload) {
            dispatch_async(dispatch_get_main_queue(), ^{
                [oldView removeFromSuperview];
                (void)oldPreload;                         // 받아 둔 뷰의 마지막 참조를 main 에서 놓는다
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

// 뷰 없이 ( 계층 밖에서 ) 배너 한 개를 받아 둔다. DaroObjCBannerView(preload:YES) 는 생성 시
// 요청을 내고, 처음 window 에 붙을 때 한 번 소비한다 — 그 부착은 LoadBanner 가 한다.
// 이미 받아 둔 뷰가 있으면 부착 전 loadAd 로 다시 요청한다: 로딩 중이거나 TTL 안의 광고가
// 있으면 SDK 가 무시하고, 만료·실패면 다시 받는다.
void DaroUnity_PreloadBanner(const char* adUnitKey, int sizeOrdinal) {
    if (!adUnitKey) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];
    DaroLogD(@"Banner", @"Preload adUnit='%@' size=%d", unit, sizeOrdinal);
    dispatch_async(s_adQueue, ^{
        DaroUnityBannerEntry* entry = s_banners[unit];
        if (!entry) return;

        // 생성은 main 에서만 하므로, 있는지 확인하고 만드는 것도 main 블록 안에서 한다 —
        // 연속 호출이 뷰를 두 개 만들지 않게.
        dispatch_async(dispatch_get_main_queue(), ^{
            __block BOOL current = NO;
            __block DaroObjCBannerView* existing = nil;
            __block DaroObjCBannerView* discarded = nil;  // 사이즈가 다른 것 — 이 main 블록에서 놓는다
            dispatch_sync(s_adQueue, ^{
                if (s_banners[unit] != entry) return;
                current = YES;
                if (entry.preloadView && entry.preloadSizeOrdinal == sizeOrdinal) {
                    existing = entry.preloadView;
                } else {
                    discarded = entry.preloadView;
                    entry.preloadView = nil;
                    entry.preloadDelegate = nil;
                }
            });
            discarded = nil;
            if (!current) return;
            if (existing) {
                [existing loadAd];
                return;
            }

            DaroObjCBannerSize nativeSize = (sizeOrdinal == 1)
                ? DaroObjCBannerSizeMrec : DaroObjCBannerSizeBanner;
            DaroUnityBannerDelegate* delegate = [DaroUnityBannerDelegate new];
            delegate.adUnitKey = unit;
            DaroObjCBannerView* view = [[DaroObjCBannerView alloc]
                initWithAdUnitKey:unit bannerSize:nativeSize preload:YES];
            view.delegate = delegate;
            DaroUnityWireBannerRevenue(view, unit);
            dispatch_sync(s_adQueue, ^{
                if (s_banners[unit] != entry) return;     // 그사이 해제됨 — 뷰는 이 블록이 끝나며 놓인다
                entry.preloadView = view;
                entry.preloadDelegate = delegate;
                entry.preloadSizeOrdinal = sizeOrdinal;
            });
        });
    });
}

void DaroUnity_LoadBanner(const char* adUnitKey, int sizeOrdinal) {
    if (!adUnitKey) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];
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

            // 받아 둔 뷰는 여기 ( main ) 에서 가져간다. PreloadBanner 는 뷰를 main 블록에서 만들어
            // 넣으므로, 같은 프레임의 Preload → Load 에서 s_adQueue 블록이 먼저 읽으면 아직 비어 있다.
            // main 큐는 FIFO 라 이 시점에는 앞선 Preload 의 뷰가 들어와 있다.
            // 사이즈가 다르면 쓰지 않고 버린다 ( 이 블록이 끝나며 main 에서 놓인다 ).
            __block DaroObjCBannerView* preloadedView = nil;
            __block DaroUnityBannerDelegate* preloadedDelegate = nil;
            __block BOOL consumesPreload = NO;
            dispatch_sync(s_adQueue, ^{
                DaroUnityBannerEntry* latest = s_banners[unit];
                if (!latest || latest.generation != generation) return;
                preloadedView = latest.preloadView;
                preloadedDelegate = latest.preloadDelegate;
                consumesPreload = preloadedView && latest.preloadSizeOrdinal == sizeOrdinal;
                latest.preloadView = nil;
                latest.preloadDelegate = nil;
            });

            DaroUnityBannerDelegate* delegate;
            DaroObjCBannerView* view;
            if (consumesPreload) {
                delegate = preloadedDelegate;
                view = preloadedView;
            } else {
                DaroObjCBannerSize nativeSize = (sizeOrdinal == 1)
                    ? DaroObjCBannerSizeMrec : DaroObjCBannerSizeBanner;
                delegate = [DaroUnityBannerDelegate new];
                delegate.adUnitKey = unit;
                // preload:NO 는 명시 loadAd 전까지 요청하지 않는다 ( 옛 autoLoad:NO 와 같다 ).
                view = [[DaroObjCBannerView alloc]
                    initWithAdUnitKey:unit bannerSize:nativeSize preload:NO];
                view.delegate = delegate;
                DaroUnityWireBannerRevenue(view, unit);
            }
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
                DaroLogD(@"Banner", @"Load.attached adUnit='%@' frame=%@ preload=%d",
                    unit, NSStringFromCGRect(view.frame), consumesPreload);
            } else if (consumesPreload) {
                // Load 직후 Hide 해서 붙이지 않는 경우. 소비는 첫 window 부착이 트리거라, 숨긴 채
                // 붙였다 바로 떼어 소비만 시킨다 — 그래야 일반 경로처럼 adLoaded 가 오고 그 뒤
                // Show 가 가능하다. 같은 런루프 턴 안이라 화면에 그려지지 않는다.
                view.hidden = YES;
                [vc.view addSubview:view];
                [view removeFromSuperview];
                view.hidden = NO;
            }
            // 받아 둔 뷰는 부착이 곧 소비다. 여기서 loadAd 를 부르면 받아 둔 광고 위에 새 요청이 겹친다.
            if (!consumesPreload) [view loadAd];
        });
    });
}

void DaroUnity_ShowBanner(const char* adUnitKey) {
    if (!adUnitKey) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];
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
            // device orientation (initial orientation only, but Show may
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

void DaroUnity_HideBanner(const char* adUnitKey) {
    if (!adUnitKey) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];
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
            __block DaroObjCAdInfo* hiddenAdInfo = nil;
            dispatch_sync(s_adQueue, ^{
                DaroUnityBannerEntry* latest = s_banners[unit];
                shouldDispatchHidden = (latest && latest.bannerView == view && !latest.visible);
                if (shouldDispatchHidden) hiddenAdInfo = latest.delegate.lastAdInfo;
            });
            if (shouldDispatchHidden) {
                DaroDispatch(unit, [NSString stringWithFormat:
                    @"{\"event\":\"adHidden\",\"adFormat\":0%@}",
                    AdInfoFields(hiddenAdInfo)]);
            }
        });
    });
}

void DaroUnity_DestroyBanner(const char* adUnitKey) {
    if (!adUnitKey) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];
    DaroLogD(@"Banner", @"Destroy adUnit='%@'", unit);
    dispatch_async(s_adQueue, ^{
        DaroObjCBannerView* view = s_banners[unit].bannerView;
        // 쓰지 않은 preload 뷰도 여기서 놓는다. 뷰가 해제되면 SDK 의 PreloadSession 이 같이
        // 해제돼 받아 둔 광고를 놓고, 늦게 오는 결과는 버려진다.
        DaroObjCBannerView* preloadView = s_banners[unit].preloadView;
        if (view || preloadView) {
            dispatch_async(dispatch_get_main_queue(), ^{
                [view removeFromSuperview];
                (void)preloadView;   // 마지막 참조를 main 에서 놓는다
            });
        }
        s_banners[unit] = nil;       // ARC releases view + delegate together
    });
}

void DaroUnity_SetBannerPosition(const char* adUnitKey, int positionOrdinal) {
    if (!adUnitKey) return;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];
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

// Banner footprint query. Returns 1 + the banner's
// on-screen rect in Unity screen px (bottom-left origin, Screen.safeArea
// convention); 0 if the banner is not attached / unknown. The view.frame is
// already safe-area-correct (BannerFrameForPosition computes it from
// parentView.safeAreaInsets), so consumers get the real footprint without
// guessing from Unity's Screen.safeArea.
//
// Unity's iOS scripting thread is the UIKit main thread, so this normally runs
// on main; the [NSThread isMainThread] guard hops to main for any off-main
// caller. s_banners is read under s_adQueue (mutated there).
int DaroUnity_GetBannerScreenRect(const char* adUnitKey,
                                  float* outX, float* outY,
                                  float* outW, float* outH) {
    if (!adUnitKey) return 0;
    NSString* unit = [NSString stringWithUTF8String:adUnitKey];

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

// Runtime teardown cleanup. Called by
// DaroUnity_DestroyAll (DaroUnityBridge.mm). Banner has no entry-level
// `destroyed` flag: banner delegates
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
            // 계층에 없어 removeFromSuperview 는 no-op 이다. 마지막 참조를 main 에서 놓으려고 넣는다.
            if (entry.preloadView) [viewsToRemove addObject:entry.preloadView];
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
