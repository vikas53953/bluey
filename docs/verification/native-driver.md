# Native QA driver receipt

- Timestamp: 2026-11-07T05:13:20Z
- Candidate source commit: `d181791a7d8a422794650d2189281891a08f3b57`
- Candidate package: `artifacts/Boo-0.1.0-candidate1-win-x64.zip`
- Candidate package SHA-256: `0209EECD180D02D5444EF6E175BA22CDD4C8A388859C61E7B17958C33E292204`
- Candidate manifest SHA-256: `88A31389B0EB81CF3AC73AB1A9D1FB51E7208A825DEB34F21CF691B2A0E9DA40`
- Candidate `Boo.dll` SHA-256: `09B5C5213358A06A9AE94434069B1929860CD624F515258F8C741D524C89879C`

## Tool and fixture evidence

- The fresh native Sky route initialized `@oai/sky` and completed
  `sky.list_windows()` / `sky.list_apps()`.
- Fixture PID `16996`, window `244516272`, was observed through Sky and
  maximized with the documented keyboard route (`Alt_L+F10`). The refreshed
  fixture state exposed the expected `Create account`, edit, Apply edit, Undo,
  status, and scroll controls.
- No Boo request, screenshot, typing, authentication, or provider action was
  performed in this receipt.

## First blocker

- Candidate PID `18904` remained responsive, but Sky returned no candidate Boo
  window or tray item. Its process had no normal main-window handle/title.
- The supported Sky API exposes targetable apps/windows, state, input, and
  accessibility actions only; it has no system-tray enumeration or tray-menu
  invocation API.
- Source inspection confirms the compact notch is intentionally
  `ShowInTaskbar=false`, `ShowActivated=false`, `WS_EX_TOOLWINDOW`,
  `WS_EX_NOACTIVATE`, and `WDA_EXCLUDEFROMCAPTURE`; the interactive panel is
  created by `App.OpenPanel`, while tray commands are owned by a WinForms
  `NotifyIcon`.

Result: native notch/panel/focus/teaching/ring/Stop/a11y behavior is
`BLOCKED` by the supported observation boundary. This is an evidence gap, not
a product pass/fail. Privacy exclusion was not disabled and no alternate UI
automation was used.
