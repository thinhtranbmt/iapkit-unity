using System;
using UnityEngine;

namespace IAPKit
{
    // -------------------------------------------------------------------------
    // Pure receipt / JWS parsing — no store handles, no Roxane types. Ported from
    // the private helpers that lived inside the old IAPManager (GetTransactionId,
    // GetPurchaseQuantity, TryParseJwsTransaction, GetSubscriptionPeriodMs).
    // -------------------------------------------------------------------------

    /// <summary>Unity unified receipt envelope ({ Payload, Store, TransactionID }).</summary>
    [Serializable]
    public class IapReceiptEnvelope
    {
        public string Payload;
        public string Store;
        public string TransactionID;
    }

    [Serializable]
    public class IapPayload
    {
        public string json;
        public string signature;
    }

    [Serializable]
    public class IapPurchaseReceipt
    {
        public int quantity;
    }

    /// <summary>Decoded StoreKit2 JWS transaction payload (Apple).</summary>
    [Serializable]
    public class AppleJwsTransaction
    {
        public long expiresDate;   // ms since epoch
        public long purchaseDate;  // ms since epoch
        public string productId;
        public string originalTransactionId;
        public string transactionId;
    }

    public static class IapReceiptParser
    {
        /// <summary>
        /// Stable per-purchase id used to dedupe recovery grants (Apple latest
        /// transaction_id / Google orderId). Empty when the receipt can't be parsed.
        /// </summary>
        public static string GetTransactionId(string receipt)
        {
            try
            {
                if (!string.IsNullOrEmpty(receipt))
                {
                    IapReceiptEnvelope env = JsonUtility.FromJson<IapReceiptEnvelope>(receipt);
                    if (env != null && !string.IsNullOrEmpty(env.TransactionID))
                    {
                        return env.TransactionID;
                    }
                }
            }
            catch { /* fall back to empty */ }

            return string.Empty;
        }

        /// <summary>Reads Store name from the unified receipt envelope ("" if absent).</summary>
        public static string GetStore(string receipt)
        {
            try
            {
                if (!string.IsNullOrEmpty(receipt))
                {
                    IapReceiptEnvelope env = JsonUtility.FromJson<IapReceiptEnvelope>(receipt);
                    if (env != null) return env.Store ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>Purchased quantity decoded from the nested payload (defaults to 1).</summary>
        public static int GetPurchaseQuantity(string receipt)
        {
            int quantity = 1;
            try
            {
                if (!string.IsNullOrEmpty(receipt))
                {
                    IapReceiptEnvelope env = JsonUtility.FromJson<IapReceiptEnvelope>(receipt);
                    if (env != null && !string.IsNullOrEmpty(env.Payload) && env.Payload != "fake")
                    {
                        IapPayload payload = JsonUtility.FromJson<IapPayload>(env.Payload);
                        if (payload != null && !string.IsNullOrEmpty(payload.json))
                        {
                            IapPurchaseReceipt receiptData = JsonUtility.FromJson<IapPurchaseReceipt>(payload.json);
                            if (receiptData != null) quantity = receiptData.quantity;
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return quantity < 1 ? 1 : quantity;
        }

        /// <summary>
        /// Decode the payload (middle segment) of a StoreKit2 JWS and return the full
        /// transaction. Optionally override expiresDate = purchaseDate + tier period.
        /// </summary>
        public static bool TryParseJwsTransaction(string jws, IIapConfig config, out AppleJwsTransaction transaction)
        {
            transaction = null;
            try
            {
                string[] parts = jws.Split('.');
                if (parts.Length != 3) return false; // not a JWS

                // Base64url -> Base64, then pad to a multiple of 4.
                string body = parts[1].Replace('-', '+').Replace('_', '/');
                switch (body.Length % 4)
                {
                    case 2: body += "=="; break;
                    case 3: body += "="; break;
                }

                string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body));
                AppleJwsTransaction txn = JsonUtility.FromJson<AppleJwsTransaction>(json);
                if (txn == null) return false;

                if (config != null && config.OverrideAppleExpiresDate && txn.purchaseDate > 0)
                {
                    long periodMs = config.ResolveSubscriptionPeriodMs(txn.productId);
                    if (periodMs > 0)
                    {
                        txn.expiresDate = txn.purchaseDate + periodMs;
                    }
                    else
                    {
                        Debug.LogWarning($"[IAPKit] Unknown subscription period for '{txn.productId}' -> keeping store expiresDate.");
                    }
                }

                transaction = txn;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IAPKit] TryParseJwsTransaction failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Read the expiresDate from a JWS transaction (after optional override).</summary>
        public static bool TryParseJwsExpiry(string jws, IIapConfig config, out DateTime expiry)
        {
            expiry = default;
            if (!TryParseJwsTransaction(jws, config, out AppleJwsTransaction txn) || txn.expiresDate <= 0)
            {
                return false;
            }

            expiry = DateTimeOffset.FromUnixTimeMilliseconds(txn.expiresDate).UtcDateTime;
            return true;
        }
    }
}
