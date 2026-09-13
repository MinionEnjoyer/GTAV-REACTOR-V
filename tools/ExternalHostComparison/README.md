# Local external-host comparison

## Current candidate safety boundary

The current fixed-build v3 driver covers Runtime, Preloader, Script and both
consumed Core DLL copies (five targets). It uses receipt schema 3 and preparation
schema 4. Historical sealed two/three-file packages do not cover this candidate;
do not reuse or modify the consumed test package. See
`../../docs/LAYERED-INPUT-TEST3.md` for the current local test and exact hashes.
The consumed Test 2 package and its frozen tools remain unchanged.

The external-host rehearsal connects a provider but keeps its window hidden.
It cannot establish that an initializer, menu or HUD appeared. Pair that
lifecycle check with `../BrowserPresentationValidation` for actual browser
desktop pixels and cold/reopen/stale/hidden regressions, then separately obtain
in-game acceptance with a newly authorized, complete reversible package.
See `../../docs/VERIFIED-WINDOW-PROMOTION-REGRESSION.md`.

## Five-target presentation candidate

`Deploy-PresentationTest.ps1` is the fixed-build five-target transaction;
it does not change any historical sealed controller driver. Use
`Test-PresentationDeployment.ps1` on a fresh private fixture directory to validate
rollback/conflict/resume behavior. `New-PresentationPackage.ps1` freezes the payload,
tools, baseline and matching deployment result into a fresh diagnostics directory.
`Run-PresentationComparison.ps1` supports the new receipt and requires a matching
deployment result plus successful package-local external-host rehearsal before Run.
It retains the user-launched game boundary, 90-second shutdown watch and automatic
restoration. Live installation is not part of preparation or rehearsal.

Do not mix the old and new receipts, reuse a consumed preparation, or update a
sealed package in place. Raw test results remain historical evidence. The desktop
controls use the frozen scripts so later repository work cannot silently change
an already prepared test.

The 2026-09-10 observer revision distinguishes authenticated desktop-probe helpers
from competing hosts using exact path/hash, retained parent, creation time and
child-mode arguments. Helpers have separate module/exit records; short-lived
helpers can still escape polling. Nonzero or unobserved helper exits are not
clean success, and unknown preloaders still disqualify. Result schema is now 2.
Real-process regression fixtures are in `../DesktopProbeValidation`.
These changed tool hashes intentionally invalidate old prepared manifests;
do not rerun the consumed comparison or rewrite its prior failed raw result.

Issue #1 diagnostic preparation, not a release or a general-purpose installer.
No product code is changed by these tools. Fixed local paths and SHA-qualified
inputs deliberately prevent applying this experiment to an arbitrary build.

## Test boundary

The user starts `Run-Comparison.ps1 -Action Run` from a normal desktop PowerShell
window, keeps it open, and launches GTA Enhanced separately into Story Mode.
The driver never launches GTA and never alters Steam overlay settings, drivers,
graphics wrappers, the original WebView2 profile, or launcher accounts.

It uses the previously qualified, receipt-backed deployment helper to install
the instrumented runtime and native-off/windowed recipe. Once one newly created
GTA process is observed at the exact expected executable path and hash, the
desktop observer launches the existing installed preloader with:

```text
--persistent-host --no-external-gpu-browser-shadow --parent-pid <verified GTA PID>
--ui-dir <installed UI> --user-data-dir <fresh private test profile>
--log-dir <private test logs> --instance-id <unique comparison ID>
```

Here `--parent-pid` means the host's actual game target, not its OS parent.
The OS parent is the desktop observer. No self-test flag, PID spoofing, injection
blocklist, process suspension, game memory patch, or elevation is used. The
installed path preserves normal preloader data-cache/game-root behavior; normal
cache/log activity is not rolled back. Profiles and evidence stay private.

The existing runtime tries its Bootstrap WebView2 proxy first. The observer
requires the exact GTA/host PID pairing in both sides' logs. A fallback is
reported in red and invalidates the comparison; it is **not prevented by a new
product patch**, and the game is not killed. Module sampling must observe one
root plus a renderer with no Steam overlay in the host/browser. Steam's DLL in
GTA alone is recorded but does not invalidate this external-process condition.
Reactor native compositor or CEF in any linked process is disqualifying.

An absent native F9 ownership event allows managed input in existing
`PreloadHandoff.ManagedOwnsF9`; the observer does not fabricate native readiness
or ownership signals. Native RuntimeReady signaling can be unavailable in this
recipe and is not automatically treated as a browser failure.

## Workflow

- `Prepare`: validate restored baseline; copy the earlier qualified offline
  payload; compile only a disposable provider fixture; seal file/tool hashes.
  Does not install or launch anything.
- `Preflight`: verify sealed tools/payload, all 1,110 installed UI files, host
  dependencies and 14-file restored baseline. Does not arm anything.
- `Rehearse`: launch the disposable target and unmodified persistent host.
  The real runtime proxy attaches from a secondary AppDomain, confirms a ready
  content generation, then disposes. Host follows actual target-process exit.
  A successful matching rehearsal is required before `Run`.
- `Run`: back up/apply, wait up to five minutes for user GTA launch, then observe
  one session for at most 30 minutes and normally 90 seconds after game exit.
  After processes close, use the qualified driver to restore both layers and
  recheck all 14 originals. One live run per prepared directory.
- `Restore`: explicit recovery using the exact live output/store receipts and
  fixed deployment driver, without depending on the staged test payload.

Keep the desktop controller open. Closing its console can terminate observation
before normal restoration; retained receipts permit explicit recovery. If any
game/host remains alive or a file changed unexpectedly, restoration refuses to
overwrite it and prints a recovery warning. No force-kill or broad deletion.

## Qualification and remaining acceptance

`Test-Comparison.ps1` covers route identity, fallback, module coverage/errors,
overlay/native/CEF presence, exact profile linkage, missing/nonzero exits and
PowerShell syntax. `Test-Deployment.ps1` rehearses apply/restore on private file
copies, including wrong-store and intervening-DLL conflict refusal. It never
executes the copied game binary or modifies the real game installation.

Offline proxy rehearsal is not Story Mode, visible pixels, GBay provider data,
user input, focus, or menu close/reopen acceptance. A staged offline preloader
cannot resolve a real GTA root for data-cache preload; that expected difference
is logged, and the live route uses the installed location. No native GPU/CEF
qualification or claim of a complete game-host equivalence is made.

Live acceptance requires the user to confirm GBay appears, its navigation works,
and close/reopen works. The collector's `qualified` flag means only its technical
checks passed. It never supplies that user acceptance automatically. Process/
module polling can miss transient loads between samples. A successful external
route is a mitigation candidate, not proof Steam caused the earlier DWM wait or
the separate remote reporter's GTA crash. Inspect logs before sharing; no dumps,
profiles, or full evidence directory are automatically uploaded or zipped.

Only the existing receipt-qualified deployment helper mutates installation files.
Do not use ALLIN1 Install/Repair during the comparison; that could overwrite the
test state. Launch through Steam normally, without rerunning an installer.
