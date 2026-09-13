# OpenNetLimit product and README hero audit

## Audit scope

This combined review covers first-run setup, live traffic, bandwidth history, the limit editor, and the light theme. Every finding comes from the five private-desktop screenshots captured during this run. It also compares the prior social card with both new README hero candidates.

The user goal is to identify the application using bandwidth and apply a limit with confidence. The accessibility target is a clear Windows desktop flow with readable states, obvious actions, and enough structure for keyboard and assistive-technology testing.

## Overall verdict

The product screenshots are credible and visually consistent. They show enough real detail to market the application without mock interface claims. The largest repository problem was presentation: the README opened with a small logo, repeated the live dashboard below the links, and left the stronger social artwork unused. The selected hero fixes that entry point and keeps later screenshots focused on different views.

## Captured steps

1. **First-run setup, healthy.** `current-run-captures/01-first-run.png` introduces the product, shows progress, and keeps the primary Next action visible. The requirements step is not shown, so its validation and error recovery could not be reviewed.
2. **Live traffic, healthy with one discoverability risk.** `current-run-captures/02-live-traffic.png` communicates connection state, current rates, a trend chart, and per-process totals in one view. The instruction says to right-click an application to manage limits. New users may miss that action because there is no visible row button.
3. **Bandwidth history, healthy.** `current-run-captures/03-bandwidth-history.png` separates received and sent traffic clearly. The process picker and hourly or daily controls are easy to find. A screenshot cannot confirm focus order or whether chart values have an accessible text alternative.
4. **Set bandwidth limit, healthy with a validation gap.** `current-run-captures/04-set-limit.png` names the target process and keeps units beside both fields. The captured state does not show invalid input, an error message, or how limits are announced to a screen reader.
5. **Light theme, healthy.** `current-run-captures/05-light-theme.png` preserves the dashboard hierarchy and product colors. Secondary text and table-row separation look lighter than the dark theme and should be checked with measured contrast before making a compliance claim.

## Hero decision

- The previous social card is polished but ties the artwork to one release number. It also gives the product name more space than the user's problem.
- Candidate 01 preserves that hierarchy but clips the longer headline. It was rejected.
- Candidate 02 gives the problem and outcome the strongest type, keeps the approved logo, and places a current product capture beside the promise. It remains readable in the saved 960px and 640px checks. It was selected.

## Evidence limits

The capture tool runs on a private desktop and confirms what each state looks like. These images do not prove keyboard order, focus visibility, screen-reader announcements, zoom behavior, live service behavior, or color contrast ratios. No live WinDivert control or system traffic rule was applied during this marketing review.

The packaged v1.0.2 executable repeated all five accepted states. Each packaged PNG is byte-identical to its source-build counterpart.
