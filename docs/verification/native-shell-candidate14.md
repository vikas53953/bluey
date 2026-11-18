# Candidate14 SHELL-001 startup/profile evidence

This is a bounded shell/profile test. It does not pass remaining native
presence, color, typing, or provider cases.

## Identity and isolated profile

- Source: `1493d18fb1133a645ee3f6ee0110ddaedf863453`.
- Candidate14 executable: `artifacts/candidate14/Boo.exe`, SHA-256
  `5E896AE774EBF582B17BDBF2C6C18A7F23526019B9088127FDDED42D02C8793E`.
- `Boo.dll`: `4D177932C60150C4618B8545A0D1C0780233BF0CF58FBF2DFB79DC80A7C2F53D`.
- Manifest: `2A253248EA8ECAFE98436343E9B0FB623EE29049AE99920D3683BBD76B3FC70F`.
- ZIP: `D96A8FBCD32985EEE7AEA529C919FD3825750D31CFF84812AB08DBDB488726A0`.
- `artifacts/qa-profile-candidate14-shell` was verified nonexistent before
  creation. No credentials or files from another profile were copied.
- The intentionally corrupt test file was created with known text bytes
  `SHELL-001-CANDIDATE14-CORRUPT-SESSION-0001`; length `43`; initial SHA-256
  `BB13C5A7BB4E073681FCB96EA631130115E9566EEC2E1806C6F1DC1D5E8D6466`.

## Launch and Sky observation

- Candidate14 launched as PID `25356` from the exact executable path using
  hidden `Start-Process`, workspace working directory, and
  `BOO_DATA_DIR=%USERPROFILE%\Downloads\blue--fo\artifacts\qa-profile-candidate14-shell`.
  Process path and executable hash were revalidated.
- Candidate14 was absent from the first `list_windows`/`list_apps` refresh and
  remained absent across five fresh polls:
  `2026-11-07T15:05:00.563Z`, `15:05:01.626Z`, `15:05:02.697Z`,
  `15:05:03.773Z`, and `15:05:04.874Z`. The process remained alive with an
  empty window title, but Sky returned no candidate14 window or app entry.
- Only unrelated/old fixture entries appeared in the broader app listing; they
  were not activated or interacted with. No candidate14 hotkey, UI state,
  screenshot, history message, input, settings, capture, microphone, or
  provider request was attempted because no candidate14 window was returned.

## SHELL-001 result and cleanup

- Candidate14 PID `25356` was stopped only after exact path revalidation.
- `sessions.db` SHA-256 after shutdown remained
  `BB13C5A7BB4E073681FCB96EA631130115E9566EEC2E1806C6F1DC1D5E8D6466`, equal
  to the pre-launch hash.

Result: the corrupt profile was preserved for this exact launch attempt. The
candidate14 UI was not observable through supported Sky, so history fallback,
normal controls, colors, restart appearance, and all other native cases are
`UNVERIFIED`, not passes.

## Normal hotkey-entry retry and appearance cases

- The same corrupt profile hash was revalidated before a second exact launch.
  Candidate14 PID `48556` was launched with hidden `Start-Process` and the
  same `BOO_DATA_DIR`. A fresh non-fixture Codex window (returned app
  `OpenAI.Codex_2p2nqsd0c76g0!App`, window `83823962`) was read through Sky;
  its state exposed accessibility fields without printing conversation text.
- One `Control_L+Alt_L+b` was sent at `2026-11-07T15:06:54.924Z`. The first
  candidate14 window/app registration was returned at
  `2026-11-07T15:07:00.956Z`: window `4263410`, title `Boo companion`, exact
  candidate14 process path. Its accessibility tree showed
  `Focus history is unavailable.` (`HistoryStatus`), native InputBox/Send,
  settings, and body/eye color controls. Sharing was off, microphone idle,
  and status `Ready`. This is the supported SHELL-001 hotkey-entry evidence;
  the earlier idle-inventory absence remains a separate limitation.
- Valid appearance values were set and saved through observed fields:
  body `#123456` at `15:07:22.385–15:07:23.527Z`, eyes `#F0F0F0` at
  `15:07:28.493–15:07:29.004Z`, and `Alt+S` at
  `15:07:34.172–15:07:34.275Z`. Accessibility status was
  `Settings saved. Credential input was protected and cleared from the UI.`
  Exact noncredential `appearance.json` became:
  `{"BodyColor":"#123456","EyeColor":"#F0F0F0"}` with hash
  `8AA0ABB263449B7094BA0FC63538D659CF8AEA19FBFF36BA8A35D0BD77B3265A`.
- Invalid values body `#777777` and eyes `#888888` were set at
  `15:07:46.023–15:07:47.128Z` and `15:07:51.481–15:07:52.223Z`; `Alt+S`
  at `15:07:57.985–15:07:58.087Z` exposed
  `Appearance was not saved: BodyColor and EyeColor must have a contrast ratio
  of at least 3:1.` The appearance file hash remained the valid hash above and
  its valid content was unchanged. The generic status text still said
  `Settings saved`, so the validation text/file hash are the authoritative
  rejection evidence.
- `Alt+R` at `15:08:09.599–15:08:09.732Z` restored accessibility values body
  `#000000`, eyes `#FFFFFF`; exact file hash became
  `09CE87EFFE71998409E22D14E42FC183DA419E9B1CDB8488A0AED0C09B172E6A` with
  matching black/white JSON.
- Persistence was checked: valid values were saved again, candidate14 PID
  `48556` was replaced only after exact path validation by PID `20516`, and one
  fresh Codex hotkey at `15:09:10.201Z` returned candidate14 window `19728818`
  at `15:09:15.133Z`. Restarted accessibility read showed body `#123456`,
  eyes `#F0F0F0`, `Focus history is unavailable.`, sharing off, and mic idle.
  `Alt+R` at `15:09:26.430–15:09:26.755Z` restored black/white. Final
  appearance JSON hash was the black/white hash above.
- Candidate14 PID `20516` was stopped after exact path validation. The corrupt
  `sessions.db` hash remained
  `BB13C5A7BB4E073681FCB96EA631130115E9566EEC2E1806C6F1DC1D5E8D6466` before
  and after shutdown. No old fixture was activated or modified; no capture,
  microphone, provider, credential, or alternate automation action occurred.

The hotkey-entry/history fallback and appearance save/reject/reset/persistence
cases are evidenced through supported accessibility/file state. Initial idle
inventory visibility, screenshot-based colors, hover, and remaining native
cases remain unverified.
