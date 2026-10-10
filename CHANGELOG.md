# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0] - 2026-10-11

First public release of the standardised middleware.

### Added
- **`IErpGateway`** between the controllers and the ERP, with `UnicontaGateway` (real
  Uniconta) and `DemoGateway` (fictional data). Chosen by `Erp:DataSource`.
- **Demo mode**: per-account fictional company (products, debtors, orders, invoices, PDFs),
  reset after 24 hours; `POST /api/demo/signup` with expiring credentials, per-IP rate limit,
  honeypot and optional Cloudflare Turnstile; CORS for the sign-up form.
- **`Orders` configuration**: country, debtor VAT code, debtor groups per customer type,
  requisition format and whether invoiced sales orders are removed.
- Swagger can be enabled outside development (`Swagger:Enabled`), with XML docs and the
  client-credential and bearer schemes.
- `Dockerfile` and `deploy/compose.demo.yaml` (non-root, read-only, dropped capabilities).
- Client expiry (`ExpiresAt`) checked at login.

### Changed
- Order items: `participants` → generic `textLines` (printed as given), `isCourseOrModule` →
  `isNonShippable`, `glsShop*` → `parcelShop*`.
- `pricesIncludeTax` now defaults to `false`: prices are sent excluding VAT, as Uniconta expects.
- `shippingProductSku` has no default; send your shipping item number.
- Rate limiting uses the real client IP behind Cloudflare when `Network:TrustCloudflareHeader` is set.

### Security
- The seeded first-run API client gets a **random** secret instead of a fixed one.

## Earlier changes

### Security

- **Fail fast on placeholder secrets.** The app now refuses to start (via
  `StartupSecrets.Require` in `Program.cs`) if `Jwt:Key` or `Auth:SecretPepper` is
  missing, still the shipped `CHANGE_THIS…` placeholder, or too short — a predictable
  signing key or pepper would let tokens be forged or client secrets brute-forced.
  Covered by `StartupSecretsTests`.
- **The Uniconta API key is no longer placed in the JWT.** Access tokens now carry
  identity only (`username` + `companyId`); the API key and password are held
  server-side in `IUnicontaCredentialStore` and never travel inside the token, which
  a client can trivially base64-decode.
- **Login now actually caches the Uniconta credentials.** Previously the credential
  store was never populated at login, so every subsequent authenticated request
  failed with "credentials expired". Credentials are now stored (encrypted, with a
  TTL) on login and slid forward on refresh.
- **Refresh tokens are hashed at rest.** `MemoryRefreshTokenStore` now persists only
  a SHA-256 of each token; the raw bearer secret is never retained.
- **The login endpoint is rate limited.** The `auth` sliding-window policy
  (5 requests/minute) is now applied to `POST /api/auth/login` via
  `[EnableRateLimiting("auth")]`; previously the policy existed but was attached to
  no endpoint.

### Fixed

- **Login now returns the refresh token** it generates, so clients can actually use
  `POST /api/auth/refresh` (the token was created and stored but never sent back).
- **`MemoryUnicontaCredentialStore.Get` is now idempotent.** It previously decrypted
  in place, corrupting the cached entry so the *second* read threw a
  `CryptographicException`. Since the client factory reads on every authenticated
  request, this broke all but the first call. `Store`/`Get` now operate on copies.

### Added

- xUnit regression tests for the token and credential subsystems:
  `JwtTokenServiceTests`, `MemoryRefreshTokenStoreTests`,
  `MemoryUnicontaCredentialStoreTests`.
- `SECURITY.md` documenting the threat model, secret handling and production
  hardening notes.

### Changed / hygiene

- Removed committed build artefacts from version control: a seeded SQLite database
  (`softcode_api.db`) and a stray `*.Backup.tmp` file. Both are covered by
  `.gitignore` going forward.
