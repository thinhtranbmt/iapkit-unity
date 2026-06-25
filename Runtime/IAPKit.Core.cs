using System;
using System.Collections.Generic;

namespace IAPKit
{
    // -------------------------------------------------------------------------
    // IAPKit.Core — Roxane-free types for a reusable Unity IAP v5 module.
    //
    // Nothing here mentions Roxane (ServiceLocator / BakerPass / InGameEvent /
    // GameData). The engine (IapPurchaser) only talks to UnityEngine.Purchasing
    // v5 + the seams declared below; each game wires its own adapter.
    // Mirrors the HttpKit / DataToolKit packaging convention: plain folder, no
    // asmdef, isolated via `namespace IAPKit`, depends only on Unity + UniTask.
    // -------------------------------------------------------------------------

    /// <summary>State machine of the purchase driver (was MyCore.IAP.IAPState).</summary>
    public enum IapState
    {
        None,
        Initializing,
        Ready,
        WaitingForUser,
        Purchasing,
        Restoring,
        ValidatingReceipt,
        GrantingContent,
        Completed,
        Failed
    }

    /// <summary>
    /// Store-neutral product kind. The app maps its own product-type enum onto this;
    /// the engine maps this onto UnityEngine.Purchasing.ProductType internally so the
    /// Kit never depends on a game-specific GameData.ProductType.
    /// </summary>
    public enum IapProductType
    {
        Consumable,
        NonConsumable,
        Subscription
    }

    /// <summary>
    /// One product to register with the store. The PLATFORM id is already resolved
    /// by the app (iOS vs Android) before it reaches the Kit — the Kit is platform
    /// agnostic about how ids are chosen, it only fetches what it is given.
    /// </summary>
    public readonly struct IapProduct
    {
        public readonly string ProductId;
        public readonly IapProductType Type;

        public IapProduct(string productId, IapProductType type)
        {
            ProductId = productId;
            Type = type;
        }
    }

    /// <summary>
    /// Game-supplied configuration for subscription expiry handling. In Roxane this is
    /// backed by ClientMiscCollection.overrideAppleExpiresDate + the tier-suffix table.
    /// </summary>
    public interface IIapConfig
    {
        /// <summary>
        /// When true, an Apple StoreKit2 transaction's expiry is recomputed as
        /// purchaseDate + ResolveSubscriptionPeriodMs(productId) instead of trusting
        /// the store-reported expiresDate (sandbox workaround).
        /// </summary>
        bool OverrideAppleExpiresDate { get; }

        /// <summary>
        /// Full duration of a subscription tier in MILLISECONDS resolved from its product id,
        /// or 0 when unknown (keep store expiresDate). Roxane resolves by id suffix
        /// (.week / .mth / .month / .year).
        /// </summary>
        long ResolveSubscriptionPeriodMs(string productId);
    }

    /// <summary>
    /// Durable, ideally cloud-synced store of "last seen store transaction id per product",
    /// used to detect auto-renewals across sessions/reinstalls. Roxane backs this with
    /// BakerPassManager (cloud model), NOT PlayerPrefs.
    /// </summary>
    public interface ISubscriptionTxnStore
    {
        string GetLastSubscriptionTxn(string productId);
        void RecordSubscriptionTransaction(string productId, string txnId);
    }

    /// <summary>Optional server/local receipt validation hook (Roxane currently returns true).</summary>
    public interface IIapReceiptValidator
    {
        bool Validate(string receipt);
    }

    /// <summary>
    /// Game-side callbacks the engine raises. Replaces the direct coupling the old
    /// IAPManager had to InGameEvent + its public C# events. The app implements this
    /// and forwards to its own event bus / ShopManager.
    /// </summary>
    public interface IIapListener
    {
        /// <summary>Engine state transition (for logging / UI gating).</summary>
        void OnStateChanged(IapState state);

        /// <summary>Show/hide a blocking spinner (was InGameEvent.OnShowLoadingAPI).</summary>
        void OnLoading(bool active);

        /// <summary>
        /// Raised after FetchPurchases completes (launch restore + after each purchase).
        /// Listeners should re-read entitlement/subscription state from the store.
        /// </summary>
        void OnSubscriptionsRefreshed();

        /// <summary>
        /// A purchase was confirmed with NO active Buy() callback waiting — i.e. the store
        /// re-delivered a purchase paid for in a previous session (app killed right after
        /// payment, before reward grant). The grant side must look up the product and award
        /// directly, deduping by transactionId. transactionId may be empty.
        /// </summary>
        void OnPurchaseRecovered(string productId, string transactionId);
    }

    /// <summary>
    /// No-op listener so callers can opt out of any subset of callbacks by subclassing.
    /// </summary>
    public class IapListenerBase : IIapListener
    {
        public virtual void OnStateChanged(IapState state) { }
        public virtual void OnLoading(bool active) { }
        public virtual void OnSubscriptionsRefreshed() { }
        public virtual void OnPurchaseRecovered(string productId, string transactionId) { }
    }

    /// <summary>Default config: never override Apple expiry, no period table.</summary>
    public sealed class DefaultIapConfig : IIapConfig
    {
        public bool OverrideAppleExpiresDate => false;
        public long ResolveSubscriptionPeriodMs(string productId) => 0;
    }

    /// <summary>In-memory txn store (fine for non-subscription games / tests).</summary>
    public sealed class InMemoryTxnStore : ISubscriptionTxnStore
    {
        private readonly Dictionary<string, string> map = new();
        public string GetLastSubscriptionTxn(string productId)
            => productId != null && map.TryGetValue(productId, out var v) ? v : string.Empty;
        public void RecordSubscriptionTransaction(string productId, string txnId)
        {
            if (!string.IsNullOrEmpty(productId))
            {
                map[productId] = txnId;
            }
        }
    }

    /// <summary>Always-valid validator (Roxane's current behaviour).</summary>
    public sealed class AlwaysValidReceiptValidator : IIapReceiptValidator
    {
        public bool Validate(string receipt) => true;
    }
}
