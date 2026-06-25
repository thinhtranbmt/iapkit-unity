// -----------------------------------------------------------------------------
// SAMPLE (NOT COMPILED) — Unity ignores any folder ending in `~`, so this file is
// reference-only. It shows how Roxane would wire IAPKit's seams to replace the old
// MyCore.IAP.IAPManager. Copy into Assets/Scripts/IAP/ (drop the `~`) for Phase 2.
//
// Mapping from the old IAPManager couplings to IAPKit seams:
//   InGameHelper.IsIos/IsAndroid ........ gone (engine uses Application.platform)
//   InGameEvent.OnShowLoadingAPI ........ IIapListener.OnLoading
//   BakerPassManager.Get/RecordTxn ...... ISubscriptionTxnStore
//   ClientMisc.overrideAppleExpiresDate . IIapConfig
//   id-suffix .week/.mth/.year .......... IIapConfig.ResolveSubscriptionPeriodMs
//   gameObject.DelayCall ................ gone (engine uses UniTask.Delay)
//   IAPProductData ...................... mapped to IapProduct below
//
// References app-specific types (ServiceLocator, BakerPassManager, InGameEvent,
// GameData, Game4Creators) that won't exist in a fresh project, so it is guarded by
// IAPKIT_SAMPLES and stays inert by default. Read it as a reference; to compile, add
// IAPKIT_SAMPLES to your Scripting Define Symbols and adapt the type names.
// -----------------------------------------------------------------------------
#if IAPKIT_SAMPLES
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game4Creators;
using Game4Creators.RewardSystem.BakerPassSystem;
using GameData;
using IAPKit;
using UnityEngine;
using UnityEngine.Purchasing;

namespace MyCore.IAP
{
    /// <summary>Config seam backed by ClientMiscCollection + Roxane tier-suffix table.</summary>
    public sealed class RoxaneIapConfig : IIapConfig
    {
        public bool OverrideAppleExpiresDate
            => ServiceLocator.Instance.GetService<ClientMiscCollection>().Data.overrideAppleExpiresDate;

        public long ResolveSubscriptionPeriodMs(string productId)
        {
            if (string.IsNullOrEmpty(productId))
            {
                return 0;
            }
            const long dayMs = 86400000L;
            if (productId.EndsWith(".week"))
            {
                return 7L * dayMs;
            }
            if (productId.EndsWith(".mth"))
            {
                return 30L * dayMs;
            }
            if (productId.EndsWith(".month"))
            {
                return 30L * dayMs;
            }
            if (productId.EndsWith(".year"))
            {
                return 365L * dayMs;
            }
            return 0;
        }
    }

    /// <summary>Cloud-synced txn store backed by the Baker Pass model (survives reinstall).</summary>
    public sealed class BakerPassTxnStore : ISubscriptionTxnStore
    {
        public string GetLastSubscriptionTxn(string productId)
            => BakerPassManager.Instance.GetLastSubscriptionTxn(productId);

        public void RecordSubscriptionTransaction(string productId, string txnId)
            => BakerPassManager.Instance.RecordSubscriptionTransaction(productId, txnId);
    }

    /// <summary>Routes engine callbacks back into Roxane's event bus + ShopManager.</summary>
    public sealed class RoxaneIapListener : IIapListener
    {
        public System.Action OnSubscriptionsRefreshedHandler;
        public System.Action<string, string> OnPurchaseRecoveredHandler;

        public void OnStateChanged(IapState state) { }
        public void OnLoading(bool active) => InGameEvent.OnShowLoadingAPI(active);
        public void OnSubscriptionsRefreshed() => OnSubscriptionsRefreshedHandler?.Invoke();
        public void OnPurchaseRecovered(string productId, string txnId) => OnPurchaseRecoveredHandler?.Invoke(productId, txnId);
    }

    /// <summary>
    /// Thin MonoBehaviour host that keeps the old IAPManager.Instance public surface so
    /// ShopManager / DebugMenuManager don't change. Delegates everything to IapPurchaser.
    /// </summary>
    public class IAPManagerKit : MonoBehaviour
    {
        public static IAPManagerKit Instance { get; private set; }

        private IapPurchaser purchaser;
        private RoxaneIapListener listener;

        public event System.Action OnSubscriptionsRefreshed;
        public event System.Action<string, string> OnPurchaseRecovered;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            listener = new RoxaneIapListener
            {
                OnSubscriptionsRefreshedHandler = () => OnSubscriptionsRefreshed?.Invoke(),
                OnPurchaseRecoveredHandler = (p, t) => OnPurchaseRecovered?.Invoke(p, t)
            };
            purchaser = new IapPurchaser(new RoxaneIapConfig(), new BakerPassTxnStore(), null, listener);
        }

        private void OnDestroy() => purchaser?.Dispose();
        private void OnApplicationFocus(bool hasFocus) { if (hasFocus) purchaser?.OnAppFocusGained(); }

        public async UniTask InitializeAsync(List<IAPProductData> productsDataConfig)
        {
            var products = new List<IapProduct>();
            if (productsDataConfig != null)
            {
                foreach (var p in productsDataConfig)
                {
                    string storeId = p.ResolvePlatformProductId();
                    if (string.IsNullOrEmpty(storeId))
                    {
                        continue;
                    }
                    products.Add(new IapProduct(storeId, MapType(p.productType)));
                }
            }

            // The legacy hard-coded iOS remove-ads product, moved out of the engine.
            if (InGameHelper.IsIos())
            {
                products.Add(new IapProduct("com.games4creators.roxane.removeads3", IapProductType.NonConsumable));
            }

            await purchaser.InitializeAsync(products);
        }

        private static IapProductType MapType(ProductType t) => t switch
        {
            // Map Roxane GameData.ProductType -> IAPKit.IapProductType.
            // Adjust to the real enum members in GameData.ProductType.
            _ => IapProductType.Consumable
        };

        // --- Pass-through API used by ShopManager / DebugMenuManager ---
        public void Buy(string id, System.Action<string> ok, System.Action<string> fail) => purchaser.Buy(id, ok, fail);
        public void UpgradeOrBuySubscription(string n, string cur, System.Action<string> ok, System.Action<string> fail) => purchaser.UpgradeOrBuySubscription(n, cur, ok, fail);
        public Product GetProduct(string id) => purchaser.GetProduct(id);
        public void RefreshPurchases() => purchaser.RefreshPurchases();
        public bool IsProductOwned(string id) => purchaser.IsProductOwned(id);
        public string GetDisplayPrice(string id) => purchaser.GetDisplayPrice(id);
        public bool TryGetSubscriptionInfo(string id, out System.DateTime e, out bool a) => purchaser.TryGetSubscriptionInfo(id, out e, out a);
    }
}

#endif
