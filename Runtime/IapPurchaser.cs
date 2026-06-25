using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.Purchasing;
using StoreProductType = UnityEngine.Purchasing.ProductType;

namespace IAPKit
{
    // -------------------------------------------------------------------------
    // IapPurchaser — the reusable Unity IAP v5 purchase engine.
    //
    // This is the Roxane-free port of MyCore.IAP.IAPManager: same Connect ->
    // FetchProducts -> FetchPurchases flow, same purchase queue, recovery-after-kill
    // detection, owned-order tracking, Google subscription upgrade, and Apple JWS
    // expiry reading — but every game-specific touch point is now a seam:
    //   - platform check        -> Application.platform (not InGameHelper)
    //   - loading spinner        -> IIapListener.OnLoading (not InGameEvent)
    //   - subscription txn store -> ISubscriptionTxnStore (not BakerPassManager)
    //   - expiry override/period -> IIapConfig (not ClientMiscCollection)
    //   - deferred timeout delay -> UniTask.Delay (not gameObject.DelayCall)
    //
    // It is a PLAIN class (like DataToolKit.DataToolService) — the app owns the
    // MonoBehaviour and forwards Unity lifecycle (OnApplicationFocus -> OnAppFocusGained).
    // -------------------------------------------------------------------------
    public class IapPurchaser
    {
        public IapState State { get; private set; } = IapState.None;

        private readonly IIapConfig config;
        private readonly ISubscriptionTxnStore txnStore;
        private readonly IIapReceiptValidator receiptValidator;
        private readonly IIapListener listener;

        private StoreController storeController;

        private readonly Queue<PurchaseRequest> purchaseQueue = new();
        private bool isProcessingQueue;

        private List<ProductDefinition> productDefinitions = new();
        private readonly Dictionary<string, ProductMetadata> productMetaDic = new();

        private Action<string> onPurchaseSuccess;
        private Action<string> onPurchaseFail;
        private string currentProductId;

        // Latest confirmed/owned order per product id (needed for Google subscription upgrades).
        private readonly Dictionary<string, Order> ownedOrders = new();

        // Transaction ids already handled by OnPurchaseConfirmed this session — the store fires
        // OnPurchaseConfirmed twice for the same restored order, so we guard against double grant.
        private readonly HashSet<string> processedConfirmedTxns = new();

        private struct PurchaseRequest
        {
            public string productId;
            public Action<string> onPurchaseSuccess;
            public Action<string> onPurchaseFail;
            public bool isUpgrade;       // Google subscription upgrade via proration
            public string oldProductId;
        }

        public IapPurchaser(
            IIapConfig config = null,
            ISubscriptionTxnStore txnStore = null,
            IIapReceiptValidator receiptValidator = null,
            IIapListener listener = null)
        {
            this.config = config ?? new DefaultIapConfig();
            this.txnStore = txnStore ?? new InMemoryTxnStore();
            this.receiptValidator = receiptValidator ?? new AlwaysValidReceiptValidator();
            this.listener = listener ?? new IapListenerBase();
        }

        private static bool IsIos() => Application.platform == RuntimePlatform.IPhonePlayer;
        private static bool IsAndroid() => Application.platform == RuntimePlatform.Android;

        #region Initialization (v5 flow)

        /// <summary>
        /// Initialize and start v5 flow:
        /// Connect() -> FetchProducts -> (OnProductsFetched) -> FetchPurchases().
        /// </summary>
        public async UniTask InitializeAsync(IReadOnlyList<IapProduct> products)
        {
            SetState(IapState.Initializing);
            productDefinitions = BuildProductDefinitions(products);

            try
            {
                await UnityServices.InitializeAsync();

#if UNITY_EDITOR
                StandardPurchasingModule.Instance().useFakeStoreUIMode = FakeStoreUIMode.StandardUser;
#endif
                storeController = UnityIAPServices.StoreController();
                SubscribeToStoreEvents();

                await storeController.Connect();
                Debug.Log("[IAPKit] Connected to store.");

                storeController.FetchProducts(productDefinitions);
                Debug.Log("[IAPKit] FetchProducts requested.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[IAPKit] InitializeAsync failed: " + ex);
                SetState(IapState.Failed);
            }
        }

        private static List<ProductDefinition> BuildProductDefinitions(IReadOnlyList<IapProduct> products)
        {
            var list = new List<ProductDefinition>();
            if (products == null) return list;

            foreach (var p in products)
            {
                if (string.IsNullOrEmpty(p.ProductId)) continue;
                list.Add(new ProductDefinition(p.ProductId, p.ProductId, ToStoreProductType(p.Type)));
            }
            return list;
        }

        private static StoreProductType ToStoreProductType(IapProductType type) => type switch
        {
            IapProductType.Subscription => StoreProductType.Subscription,
            IapProductType.NonConsumable => StoreProductType.NonConsumable,
            _ => StoreProductType.Consumable
        };

        #endregion

        #region Event subscription

        private void SubscribeToStoreEvents()
        {
            if (storeController == null) return;

            storeController.OnProductsFetched += OnProductsFetched;
            storeController.OnProductsFetchFailed += OnProductsFetchFailed;
            storeController.OnPurchasesFetched += OnPurchasesFetched;
            storeController.OnPurchasesFetchFailed += OnPurchasesFetchFailed;
            storeController.OnPurchasePending += OnPurchasePending;
            storeController.OnPurchaseDeferred += OnPurchaseDeferred;
            storeController.OnPurchaseConfirmed += OnPurchaseConfirmed;
            storeController.OnPurchaseFailed += OnPurchaseFailed;
            storeController.OnStoreDisconnected += OnStoreDisconnected;
        }

        /// <summary>Call from the host MonoBehaviour's OnDestroy.</summary>
        public void Dispose()
        {
            if (storeController == null) return;
            try
            {
                storeController.OnProductsFetched -= OnProductsFetched;
                storeController.OnProductsFetchFailed -= OnProductsFetchFailed;
                storeController.OnPurchasesFetched -= OnPurchasesFetched;
                storeController.OnPurchasesFetchFailed -= OnPurchasesFetchFailed;
                storeController.OnPurchasePending -= OnPurchasePending;
                storeController.OnPurchaseDeferred -= OnPurchaseDeferred;
                storeController.OnPurchaseConfirmed -= OnPurchaseConfirmed;
                storeController.OnPurchaseFailed -= OnPurchaseFailed;
                storeController.OnStoreDisconnected -= OnStoreDisconnected;
            }
            catch { /* swallow unsubscribe issues */ }
        }

        #endregion

        #region Public API

        /// <summary>Enqueue a buy request (processed sequentially).</summary>
        public void Buy(string productId, Action<string> onSuccess, Action<string> onFail)
        {
            if (string.IsNullOrEmpty(productId)) return;

            purchaseQueue.Enqueue(new PurchaseRequest
            {
                productId = productId,
                onPurchaseSuccess = onSuccess,
                onPurchaseFail = onFail
            });
            _ = ProcessPurchaseQueue();
        }

        /// <summary>
        /// Buy a subscription, or upgrade an existing one.
        /// - No current subscription -> normal purchase.
        /// - Android with current subscription -> Google upgrade with proration.
        /// - iOS with current subscription -> normal purchase (Apple upgrades within the group).
        /// </summary>
        public void UpgradeOrBuySubscription(string newProductId, string currentOwnedProductId, Action<string> onSuccess, Action<string> onFail)
        {
            if (string.IsNullOrEmpty(newProductId))
            {
                onFail?.Invoke(newProductId);
                return;
            }

            bool hasCurrent = !string.IsNullOrEmpty(currentOwnedProductId);
            bool isAndroidUpgrade = hasCurrent && IsAndroid() && ownedOrders.ContainsKey(currentOwnedProductId);

            purchaseQueue.Enqueue(new PurchaseRequest
            {
                productId = newProductId,
                onPurchaseSuccess = onSuccess,
                onPurchaseFail = onFail,
                isUpgrade = isAndroidUpgrade,
                oldProductId = currentOwnedProductId
            });
            _ = ProcessPurchaseQueue();
        }

        public Product GetProduct(string productId) => storeController?.GetProductById(productId);

        /// <summary>Manually re-query owned purchases. Fires OnPurchasesFetched on completion.</summary>
        public void RefreshPurchases()
        {
            if (storeController == null || State != IapState.Ready)
            {
                Debug.Log($"[IAPKit] RefreshPurchases skipped (storeController={(storeController != null)} state={State})");
                return;
            }

            try
            {
                storeController.FetchPurchases();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[IAPKit] RefreshPurchases threw: " + ex);
            }
        }

        /// <summary>
        /// Forward the host MonoBehaviour's OnApplicationFocus(true) here so background
        /// auto-renewals (e.g. sandbox 5-min renewals) get picked up on resume.
        /// </summary>
        public void OnAppFocusGained() => RefreshPurchases();

        /// <summary>True when the store reported this product as an owned/confirmed order.</summary>
        public bool IsProductOwned(string productId)
            => !string.IsNullOrEmpty(productId) && ownedOrders.ContainsKey(productId);

        public string GetDisplayPrice(string productId)
            => productMetaDic.TryGetValue(productId, out var meta) ? meta?.localizedPriceString : string.Empty;

        /// <summary>Read the live subscription state for a product from the store receipt.</summary>
        public bool TryGetSubscriptionInfo(string productId, out DateTime expiry, out bool isActive)
        {
            expiry = default;
            isActive = false;
            if (string.IsNullOrEmpty(productId)) return false;

            string receipt = null;
            ownedOrders.TryGetValue(productId, out Order ownedOrder);
            if (ownedOrder != null) receipt = ownedOrder.Info?.Receipt;

            // Apple / StoreKit2: unified Receipt is often empty after reinstall and
            // SubscriptionManager can't parse StoreKit2 anyway — read expiry from the JWS.
            if (IsIos() && ownedOrder?.Info is IAppleOrderInfo appleInfo)
            {
                string jws = appleInfo.jwsRepresentation;
                if (!string.IsNullOrEmpty(jws) && IapReceiptParser.TryParseJwsExpiry(jws, config, out expiry))
                {
                    isActive = expiry > DateTime.UtcNow;
                    return true;
                }
            }

            if (string.IsNullOrEmpty(receipt))
            {
                Product product = storeController?.GetProductById(productId);
                if (product != null && product.hasReceipt) receipt = product.receipt;
            }

            if (string.IsNullOrEmpty(receipt))
            {
                Debug.LogWarning($"[IAPKit] TryGetSubscriptionInfo: no receipt for '{productId}'.");
                return false;
            }

            try
            {
                var manager = new SubscriptionManager(receipt, productId, null);
                SubscriptionInfo info = manager.getSubscriptionInfo();
                if (info == null) return false;

                isActive = info.IsSubscribed() == Result.True;
                expiry = info.GetExpireDate();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IAPKit] TryGetSubscriptionInfo failed for " + productId + " : " + e.Message);
                return false;
            }
        }

        #endregion

        #region Purchase queue

        private void OnPurchaseFailCallback(string productId)
        {
            onPurchaseFail?.Invoke(productId);
            onPurchaseFail = null;
            currentProductId = string.Empty;
        }

        private void OnPurchaseSuccessCallback(string productId)
        {
            onPurchaseSuccess?.Invoke(productId);
            onPurchaseSuccess = null;
            currentProductId = string.Empty;
        }

        private async UniTask ProcessPurchaseQueue()
        {
            if (isProcessingQueue) return;
            isProcessingQueue = true;

            try
            {
                while (purchaseQueue.Count > 0)
                {
                    await WaitForState(IapState.Ready);

                    var req = purchaseQueue.Dequeue();
                    onPurchaseSuccess = req.onPurchaseSuccess;
                    onPurchaseFail = req.onPurchaseFail;
                    currentProductId = req.productId;

                    if (string.IsNullOrEmpty(req.productId))
                    {
                        OnPurchaseFailCallback(req.productId);
                        continue;
                    }

                    var product = storeController?.GetProductById(req.productId);
                    if (product == null)
                    {
                        OnPurchaseFailCallback(req.productId);
                        Debug.LogError("[IAPKit] Queued product not found: " + req.productId);
                        continue;
                    }

                    SetState(IapState.Purchasing);
                    try
                    {
                        if (req.isUpgrade && IsAndroid()
                            && ownedOrders.TryGetValue(req.oldProductId, out Order oldOrder)
                            && storeController.GooglePlayStoreExtendedPurchaseService != null)
                        {
                            // WithTimeProration (not ChargeProratedPrice): switches immediately and
                            // credits unused time, and works across billing periods (week/month/year).
                            storeController.GooglePlayStoreExtendedPurchaseService.UpgradeDowngradeSubscription(
                                oldOrder, product, GooglePlayReplacementMode.WithTimeProration);
                        }
                        else
                        {
                            storeController.PurchaseProduct(req.productId);
                        }
                        await WaitForState(IapState.Ready);
                    }
                    catch (Exception ex)
                    {
                        OnPurchaseFailCallback(req.productId);
                        Debug.LogError("[IAPKit] Failed to start purchase: " + ex);
                        ResetToReady();
                    }
                }
            }
            finally
            {
                isProcessingQueue = false;
            }
        }

        private UniTask WaitForState(IapState desired)
        {
            var tcs = new UniTaskCompletionSource<bool>();

            async void Poll()
            {
                while (State != desired)
                {
                    await UniTask.Delay(50);
                }
                tcs.TrySetResult(true);
            }

            Poll();
            return tcs.Task;
        }

        #endregion

        #region Store event handlers

        private void OnProductsFetched(List<Product> products)
        {
            Debug.Log($"[IAPKit] OnProductsFetched: {products?.Count ?? 0}");
            if (products != null)
            {
                foreach (var product in products)
                {
                    productMetaDic.TryAdd(product.definition.id, product.metadata);
                }
            }

            try
            {
                storeController.FetchPurchases();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[IAPKit] FetchPurchases threw: " + ex);
                ResetToReady();
            }
        }

        private void OnProductsFetchFailed(ProductFetchFailed fetchFailed)
        {
            Debug.LogError($"[IAPKit] OnProductsFetchFailed : {fetchFailed.FailureReason}");
            ResetToReady();
        }

        private void OnPurchasesFetched(Orders orders)
        {
            if (orders?.PendingOrders != null)
            {
                foreach (var order in orders.PendingOrders)
                {
                    storeController.ConfirmPurchase(order);
                }
            }

            if (orders?.ConfirmedOrders != null)
            {
                foreach (var order in orders.ConfirmedOrders)
                {
                    TryTrackOwnedOrder(order);
                }
            }

            ResetToReady();

            try
            {
                listener.OnSubscriptionsRefreshed();
            }
            catch (Exception e)
            {
                Debug.LogError("[IAPKit] OnSubscriptionsRefreshed handler threw: " + e);
            }
        }

        private void OnPurchasesFetchFailed(PurchasesFetchFailureDescription failure)
        {
            Debug.LogError("[IAPKit] OnPurchasesFetchFailed : " + failure?.message);
            ResetToReady();
        }

        private void OnPurchasePending(PendingOrder order)
        {
            if (order == null)
            {
                ResetToReady();
                return;
            }
            storeController.ConfirmPurchase(order);
        }

        private void OnPurchaseDeferred(DeferredOrder deferred)
        {
            Debug.LogWarning("[IAPKit] OnPurchaseDeferred: awaiting approval");
            SetState(IapState.WaitingForUser);
            // Do NOT confirm here. Time out the wait after 5s if still pending.
            DeferredTimeout().Forget();
        }

        private async UniTaskVoid DeferredTimeout()
        {
            await UniTask.Delay(5000);
            if (State == IapState.WaitingForUser)
            {
                SetState(IapState.Ready);
            }
        }

        private void OnPurchaseConfirmed(Order order)
        {
            if (order?.Info == null)
            {
                OnPurchaseFailCallback(currentProductId);
                Debug.LogWarning("[IAPKit] OnPurchaseConfirmed: null");
                ResetToReady();
                return;
            }

            if (order.Info.PurchasedProductInfo.Count > 0)
            {
                string productId = order.Info.PurchasedProductInfo[0].productId;
                string txnId = IapReceiptParser.GetTransactionId(order.Info?.Receipt);

                // Guard the duplicate OnPurchaseConfirmed fire for the same order, else the
                // second fire would fall into the recovery branch and grant the reward twice.
                if (!string.IsNullOrEmpty(txnId) && !processedConfirmedTxns.Add(txnId))
                {
                    ResetToReady();
                    return;
                }

                TryTrackOwnedOrder(order);

                // Captured BEFORE OnPurchaseSuccessCallback nulls it. False = no active Buy()
                // this session => store re-delivered a previous-session purchase (kill-after-pay).
                bool hasActiveBuyCallback = onPurchaseSuccess != null;

                OnPurchaseSuccessCallback(productId);

                if (!hasActiveBuyCallback)
                {
                    try
                    {
                        listener.OnPurchaseRecovered(productId, txnId);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("[IAPKit] OnPurchaseRecovered handler threw: " + e);
                    }
                }

                ResetToReady();
            }
            else
            {
                OnPurchaseFailCallback(currentProductId);
                ResetToReady();
            }
        }

        private void OnPurchaseFailed(FailedOrder order)
        {
            listener.OnLoading(false);

            if (order?.Info?.PurchasedProductInfo == null || order.Info.PurchasedProductInfo.Count == 0)
            {
                Debug.LogError("[IAPKit] OnPurchaseFailed : no product info available " + order?.Details);
                ResetToReady();
                return;
            }

            string productId = order.Info.PurchasedProductInfo[0].productId;
            Debug.LogError($"[IAPKit] OnPurchaseFailed product={productId} reason={order.FailureReason} message={order.Details}");

            // Surface the failure to the waiting Buy() caller so UI doesn't hang.
            OnPurchaseFailCallback(productId);
            ResetToReady();
        }

        private void OnStoreDisconnected(StoreConnectionFailureDescription desc)
        {
            Debug.LogWarning("[IAPKit] OnStoreDisconnected: " + desc);
            SetState(IapState.Failed);
        }

        #endregion

        #region Utilities / state

        private void ResetToReady() => ResetToReadyAsync().Forget();

        private async UniTaskVoid ResetToReadyAsync()
        {
            await UniTask.Yield();
            SetState(IapState.Ready);
        }

        private void SetState(IapState s)
        {
            State = s;
            try { listener.OnStateChanged(s); } catch { /* ignore listener faults */ }
        }

        private void TryTrackOwnedOrder(Order order)
        {
            try
            {
                if (order?.Info?.PurchasedProductInfo != null && order.Info.PurchasedProductInfo.Count > 0)
                {
                    string productId = order.Info.PurchasedProductInfo[0].productId;
                    if (!string.IsNullOrEmpty(productId))
                    {
                        ownedOrders[productId] = order;
                        DetectAndLogRenewal(productId, order.Info?.Receipt);
                    }
                }
            }
            catch { /* ignore tracking failures */ }
        }

        /// <summary>
        /// Reads the unified-receipt transaction id (changes on every renewal) and compares
        /// it to the last persisted one for this product via ISubscriptionTxnStore. A different
        /// id = the store delivered a new (renewed) purchase. Persists the new baseline.
        /// </summary>
        private void DetectAndLogRenewal(string productId, string receipt)
        {
            if (string.IsNullOrEmpty(productId)) return;

            string txnId = IapReceiptParser.GetTransactionId(receipt);
            if (string.IsNullOrEmpty(txnId)) return;

            string prevTxn = txnStore.GetLastSubscriptionTxn(productId);
            if (txnId == prevTxn) return; // unchanged

            txnStore.RecordSubscriptionTransaction(productId, txnId);

            if (string.IsNullOrEmpty(prevTxn)) return; // first sighting -> baseline only

            Debug.LogWarning($"[IAPKit][AUTO-RENEW] product={productId} renewed/upgraded! prevTxn={prevTxn} -> newTxn={txnId}");
        }

        #endregion
    }
}
