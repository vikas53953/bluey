# Shell recovery integration

Root integrated the two isolated builders against `e7047727fbdd59c854b375792797559a213db3cd`. Only MainWindow, NotchWindow and AppearanceStore production code changed. The independent appearance-path RED regression was frozen before implementation; no existing proof was changed.

After integration, the full Release suite passed **242 tests with one unchanged host-dependent skip**. Core TRX: `TestResults/vikasmit_VIKASMIT_2026-11-07_20_28_20_net10.0.trx` (144 passed, 1 skipped). Integration TRX: `TestResults/vikasmit_VIKASMIT_2026-11-07_20_28_23_net10.0.trx` (98 passed). The Release app build passed with zero warnings/errors. Oracle revision 16 verified all 43 frozen files; `git diff --check` passed.

The shell now contains optional history initialization/read/save failures, replaces disconnected-monitor geometry with a live fallback after stopping work, and reveals error text only on hover when active disclosure is absent. The appearance reader distinguishes missing paths from failed reads. Root additionally removed an unjustified preservation claim from the error message and handled invalid numeric history formatting.

These are source/integration results. SHELL-001 through SHELL-007 and the outstanding presence/whole-product native cases are not passed by this receipt. Candidate13's field mutation belongs to its older exact source and is not a runtime pass for this repair.
