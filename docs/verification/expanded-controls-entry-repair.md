# Expanded controls keyboard recovery

Work source: frozen NAT-023 keyboard access and the accepted main plan's
settled decision that the taskbar is not Boo's primary home. Independent
Advisor review found no settled-decision conflict in a temporary normal
window entry for expanded controls.

Only MainWindow becomes a normal taskbar/Alt+Tab window while visible. Its
existing close path hides it, removing that entry. The resident pocket notch
stays off the taskbar. Permanent capture exclusion remains on both windows.
This repairs conventional keyboard recovery and does not establish that the
desktop inspection tool can observe a protected window.

This is a native/manual surface covered by the pre-existing frozen test;
no implementation-mirroring unit test is added. Verify Release build and
directly inspect open/hide/reopen on the next exact package. Record native
results honestly, including any observation limitation.
