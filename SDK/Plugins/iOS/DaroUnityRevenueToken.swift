import Foundation
import CryptoKit

/// Derives the capability token for `DaroObjCAds.registerPlugin` — the Unity-side
/// equivalent of `DaroRevenuePlugin`'s identifier. Mirrors DaroSDK's `DaroCrypto`
/// wire format exactly: `base64( nonce[12] || AES-GCM-ciphertext || tag[16] )`
/// with `key = SHA256(appKey)`. `appKey` is read from the same Info.plist entry
/// the native SDK gates on (`DaroAppKey`, falling back to `DaroAdsKey`).
///
/// Exposed as a C symbol via `@_cdecl` so the ObjC++ shim can call it without a
/// generated `-Swift.h` import. The returned pointer is `strdup`'d; the caller
/// must `free()` it. Returns `nil` when the app key is missing.
@_cdecl("DaroDerivePaidEventToken")
public func DaroDerivePaidEventToken() -> UnsafeMutablePointer<CChar>? {
    let appKey = ["DaroAppKey", "DaroAdsKey"].lazy
        .compactMap { Bundle.main.object(forInfoDictionaryKey: $0) as? String }
        .first { !$0.isEmpty }

    guard let appKey = appKey else { return nil }

    let symmetricKey = SymmetricKey(data: SHA256.hash(data: Data(appKey.utf8)))
    guard let sealed = try? AES.GCM.seal(Data("adPaidEvent".utf8), using: symmetricKey) else {
        return nil
    }

    var combined = Data()
    combined.append(contentsOf: sealed.nonce)
    combined.append(sealed.ciphertext)
    combined.append(sealed.tag)
    return strdup(combined.base64EncodedString())
}
