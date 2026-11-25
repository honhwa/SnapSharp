# Live vertical capture sample

- Captured: 2026-11-25T15:53:12.7667866+08:00.
- Baseline: `ffc7bbf01410801e4b23f9dbf10201ee6ce90d94` plus working-tree ScrollingChecks acceptance changes; desktop application version 1.1.7.
- Application: `msedge.exe` 149.0.4022.69; Microsoft Windows NT 10.0.26300.0; 96 DPI.
- Capture: production ScrollCaptureWindow manual input and Auto backend selected WGC; 600 x 320 physical pixels.
- Fixture: local public-only article.html; no user browser profile or external page.
- Document row positions: `[252, 152, 252, 352]`; signed offsets: `[-100, 100, 100]`.
- Output: 600 x 520; all rows compared against a union built using absolute document-Y colors, independently of Match/Stitch.
- Fixed navigation was outside the selected region. This does not verify automatic fixed-bar detection, arbitrary pages, Electron, Office, mixed DPI or other graphics drivers.

The source runner also passed manual recapture cancellation, successful upward recapture and GDI automatic capture to page end. Complete temporary evidence: `.artifacts/scrolling-checks/a2caf6c63af34ca0a7fe9099dfa9ee29/desktop-browser/report.json` (not distributed).
