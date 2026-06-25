# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-06-25
### Added
- Initial release.
- `IapPurchaser` engine (Unity Purchasing v5): Connect → FetchProducts → FetchPurchases, sequential purchase queue, kill-after-pay recovery, owned-order tracking, Google subscription upgrade (WithTimeProration), Apple StoreKit2 JWS expiry, auto-renew detection.
- Seams: `IIapConfig`, `ISubscriptionTxnStore`, `IIapReceiptValidator`, `IIapListener` (+ defaults).
- `IapReceiptParser` — pure receipt / JWS parsing.
- `Roxane Adapter` sample (template).
