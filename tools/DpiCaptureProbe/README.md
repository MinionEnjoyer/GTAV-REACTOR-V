# Off-screen DPI capture regression

Tests the actual WebView2 composition host and the candidate's capture-size
policy at 24 resolution/scale combinations. No GTA launch, game file edits,
screen capture, or Windows display configuration changes. It creates its own
off-screen window and browser profile; only local static HTML is loaded.

Build the Runtime and this project with `dotnet build -c Release`. Run
`DpiCaptureProbe.exe` with two absolute path arguments: the Runtime DLL and a
fresh results directory. Launch it with PowerShell `Start-Process -WindowStyle
Hidden -PassThru`, quoting each argument if its path contains spaces. Poll/wait
for that process to exit before inspecting `results.txt`.

Exit codes: 0 all captures accepted, 2 size-policy rejection, 1 exception,
64 missing arguments. Existing results are never overwritten. A baseline DLL
without the new policy uses exact equality and is expected to reject some
cases. Keep its results separate from the candidate run.

This is a host capture regression, not full initializer/menu or in-game proof.
Generation/paint-identity, foreground ownership, and actual desktop presentation
remain separate required checks. Retain only `results.txt` when sharing the
probe result; the generated browser profile is not part of the report.
