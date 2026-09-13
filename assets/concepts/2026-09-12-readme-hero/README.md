# OpenNetLimit README hero review

This folder preserves the full decision trail for the README hero published with v1.0.2.

## Decision

`readme-hero-candidate-02-product-led.png` is selected. It leads with the user's problem, keeps the approved crossing-lanes identity untouched, and shows the real live-traffic dashboard. The artwork contains no release number, so it won't become stale with the next package update.

`readme-hero-candidate-01-continuity.png` is retained as a rejected study. Its longer headline clips before the final word and the product message loses clarity at narrower README widths.

The selected file is copied byte-for-byte to:

- `readme-hero-final.png`
- `../../marketing/readme-hero.png`
- `../../marketing/social-preview.png`

The root README references `assets/marketing/readme-hero.png` once, as its first content. It does not repeat the live-dashboard screenshot farther down the page.

## Preserved material

- `README-before.md` and `README-after.md` record the documentation change.
- `source/approved-logo-master.png` is the untouched approved logo.
- `source/previous-social-preview.png` is the earlier production artwork with its release footer.
- `current-run-captures/` contains all five private-desktop screenshots used for this review.
- `packaged-captures/` contains the same five states from the packaged v1.0.2 executable. Every PNG matches the source-build capture byte-for-byte.
- `review/` contains both 960px and 640px checks, a candidate comparison, and a reference comparison.
- `audit.md` records the product-flow findings and the visual decision.
- `selection.json` and `render-report.json` record hashes and production status.

## Rebuild

From the repository root:

```powershell
dotnet run --project tools/OpenNetLimit.MarketingHero/OpenNetLimit.MarketingHero.csproj -c Release -- --repo . --select 2
```

The renderer uses only the approved master and the current product capture. It regenerates both concepts, the comparison sheets, the selected final, and the production artwork.
