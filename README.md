# IAPKit

A reusable Unity **In-App Purchasing v5** module, extracted from a production game's IAP manager.
Zero game-specific dependencies — the engine talks only to **Unity Purchasing v5 + UniTask** and a
small set of seams (`namespace IAPKit`), so each project wires its own adapter.

## What it does

Full v5 flow `Connect → FetchProducts → FetchPurchases`, plus:
- Sequential purchase **queue** (`Buy`, `UpgradeOrBuySubscription`).
- Pending / Deferred / Confirmed / Failed order handling.
- **Recovery-after-kill** — store re-delivers a purchase paid for in a previous session ⇒
  `IIapListener.OnPurchaseRecovered(productId, txnId)`, deduped by transaction id.
- **Owned-order tracking** + Google subscription **upgrade** (`WithTimeProration`).
- Apple **StoreKit2 JWS** expiry reading + `SubscriptionManager` fallback.
- **Auto-renew detection** across sessions via `ISubscriptionTxnStore`.

## Requirements

| Dependency | How it resolves |
|---|---|
| Unity 2021.3+ | — |
| **Unity Purchasing** (`com.unity.purchasing` 5.x) | Auto-resolved — declared in `package.json` (Unity registry). Pulls `com.unity.services.core` transitively. |
| **UniTask** (`com.cysharp.unitask`) | **Must be installed separately.** UniTask is distributed via Git/OpenUPM, not the Unity registry, so UPM can't auto-resolve it. See below. |

### Installing UniTask (required, do this first)
Add to your project's `Packages/manifest.json`:
```json
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask"
```

## Install

### Package Manager UI
`Window ▸ Package Manager ▸ + ▸ Add package from git URL…`
```
https://github.com/thinhtranbmt/iapkit-unity.git#v0.1.0
```

### manifest.json
```json
"com.mycore.iapkit": "https://github.com/thinhtranbmt/iapkit-unity.git#v0.1.0"
```
Drop `#v0.1.0` to track `main`.

## API surface

| File | Role |
|---|---|
| `IAPKit.Core.cs` | Enums (`IapState`, `IapProductType`), `IapProduct`, and the seams: `IIapConfig`, `ISubscriptionTxnStore`, `IIapReceiptValidator`, `IIapListener` + default impls. |
| `IapReceiptParser.cs` | Pure receipt / JWS parsing (txn id, store, quantity, Apple expiry). No store handles. |
| `IapPurchaser.cs` | The engine. **Plain class** (not a MonoBehaviour) — the app owns the MonoBehaviour and forwards `OnApplicationFocus → OnAppFocusGained()`. |

## Seams (what the app supplies)

| Seam | Purpose | Default impl |
|---|---|---|
| `IIapConfig` | Apple expiry override + subscription period per product id | `DefaultIapConfig` (no override) |
| `ISubscriptionTxnStore` | Durable "last seen txn per product" for auto-renew detection | `InMemoryTxnStore` |
| `IIapReceiptValidator` | Server/local receipt validation hook | `AlwaysValidReceiptValidator` |
| `IIapListener` | Engine callbacks (state, loading spinner, refreshed, recovered) | `IapListenerBase` (no-op) |

All seams have defaults, so a new project can start with `new IapPurchaser()` and add only what it needs.

## Minimal usage

```csharp
using IAPKit;

var purchaser = new IapPurchaser(config, txnStore, validator: null, listener);

await purchaser.InitializeAsync(new List<IapProduct> {
    new IapProduct("coin_500",        IapProductType.Consumable),
    new IapProduct("bakerpass.week",  IapProductType.Subscription),
});

purchaser.Buy("coin_500", onSuccess: id => Grant(id), onFail: id => { /* surface to UI */ });

// from your MonoBehaviour:
//   void OnApplicationFocus(bool f) { if (f) purchaser.OnAppFocusGained(); }  // catch background renewals
//   void OnDestroy()                { purchaser.Dispose(); }
```

## Samples
Import **Roxane Adapter (template)** from the Package Manager (`Samples` tab) for an app-side
wiring example. It references app-specific types so it is guarded by the `IAPKIT_SAMPLES`
define and stays inert until you add that symbol and adapt the type names.

## Notes
- Verify on **device** against sandbox (real purchase, kill-after-pay recovery, subscription
  upgrade, auto-renew). The Editor Fake Store only covers the happy path.

## License
See [LICENSE.md](LICENSE.md).
