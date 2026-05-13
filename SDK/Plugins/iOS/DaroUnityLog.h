// Daro Unity SDK — iOS shim log helper.
//
// Mirror of Kotlin `SDK/Plugins/Android/DaroLog.kt`. Same level integers
// (0=None / 1=Error / 2=Warn / 3=Info / 4=Verbose) — these are the raw
// values of C# `Daro.DaroLogLevel`, sent across the JNI/DllImport boundary
// without any collapse. The 5→3 mapping to daro iOS internal
// `DaroObjCLogLevel` (off/error/debug) lives below in
// `DaroUnityCollapseToObjCLogLevel` so the C# layer never sees iOS-specific
// bridging shapes.
//
// Macros gate at call site — args inside the NSLog are evaluated only when
// the level allows. Mirrors Kotlin `inline fun daroD/daroW { ... }` lambda
// arg-skip semantics.
//
// Forbidden direct calls in `SDK/Plugins/iOS/*.mm`: `NSLog`, `os_log`,
// `fprintf(stderr, ...)`. See `.claude/rules/logging.md`.

#ifndef DARO_UNITY_LOG_H
#define DARO_UNITY_LOG_H

#import <Foundation/Foundation.h>
#import <stdatomic.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef NS_ENUM(NSInteger, DaroUnityLogLevelValue) {
    DaroUnityLogLevelNone    = 0,
    DaroUnityLogLevelError   = 1,
    DaroUnityLogLevelWarn    = 2,
    DaroUnityLogLevelInfo    = 3,
    DaroUnityLogLevelVerbose = 4,
};

// Cross-thread visibility: Unity main thread writes via SetLogLevel while
// callback queues / serial dispatch queues read. `_Atomic(int)` mirrors
// Kotlin's `@Volatile Int` — relaxed-memory loads suffice for a gate
// predicate where one-tick drift is harmless.
extern _Atomic(int) gDaroUnityLogLevel;

// Read helper — relaxed atomic load. Hot path; inlined.
NS_INLINE int DaroUnityLogGetLevel(void) {
    return atomic_load_explicit(&gDaroUnityLogLevel, memory_order_relaxed);
}

// Setter — single source of truth for all level updates. Used by both
// `DaroUnity_Initialize` and `DaroUnity_SetLogLevel` in DaroUnityBridge.mm.
void DaroUnityLogSetLevel(int level);

// 5→3 collapse: maps raw `Daro.DaroLogLevel` (0..4) to the daro iOS
// internal `DaroObjCLogLevel` raw value (0=off / 1=error / 2=debug). The
// daro iOS Bridge has no distinct warning level so Warn raises with errors;
// Verbose has no distinct level so collapses to debug. Returns int (not
// `DaroObjCLogLevel`) to avoid leaking the daro iOS type into this header.
int DaroUnityCollapseToObjCLogLevel(int level);

#ifdef __cplusplus
}
#endif

// ── Macros ──────────────────────────────────────────────────────────────
//
// `area` is an NSString * literal (e.g. @"Native"), `fmt` is an NSString
// format literal (e.g. @"foo %d"). Adjacent NSString literals concatenate at
// compile time per the Objective-C grammar, so `@"[Daro:%@] " fmt` becomes
// a single literal when `fmt` is a string-literal expression.

#define DaroLogD(area, fmt, ...) \
    do { \
        if (DaroUnityLogGetLevel() >= DaroUnityLogLevelVerbose) { \
            NSLog(@"[Daro:%@] " fmt, area, ##__VA_ARGS__); \
        } \
    } while (0)

#define DaroLogW(area, fmt, ...) \
    do { \
        if (DaroUnityLogGetLevel() >= DaroUnityLogLevelWarn) { \
            NSLog(@"[Daro:%@] " fmt, area, ##__VA_ARGS__); \
        } \
    } while (0)

#endif  // DARO_UNITY_LOG_H
