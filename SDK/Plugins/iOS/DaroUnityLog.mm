// Daro Unity SDK — iOS shim log helper definitions.
//
// See `DaroUnityLog.h` for surface and intent. This .mm holds the shared
// level state and the 5→3 collapse for daro iOS internal `DaroObjCLogLevel`.

#import "DaroUnityLog.h"

_Atomic(int) gDaroUnityLogLevel = DaroUnityLogLevelNone;

void DaroUnityLogSetLevel(int level) {
    atomic_store_explicit(&gDaroUnityLogLevel, level, memory_order_relaxed);
}

int DaroUnityCollapseToObjCLogLevel(int level) {
    // daro iOS `DaroObjCLogLevel`: off=0 / error=1 / debug=2. C#
    // `DaroLogLevel` 5-step folds to these three. Mirrors the table that
    // used to live in C# `DaroIOSEncoding.LogLevelToNative` (removed in
    // log-module-ios sprint).
    switch (level) {
        case DaroUnityLogLevelNone:    return 0;  // off
        case DaroUnityLogLevelError:   return 1;  // error
        case DaroUnityLogLevelWarn:    return 1;  // error  — Bridge has no warn
        case DaroUnityLogLevelInfo:    return 2;  // debug  — Bridge's most verbose
        case DaroUnityLogLevelVerbose: return 2;  // debug
        default:                       return 0;  // unknown → quiet
    }
}
