# Annotation exclusion repair

Pre-existing acceptance case: NAT-008 in the frozen native matrix, authored
before the implementation. Its expected failure behavior is to stop capture
if any Boo-owned window cannot be excluded.

Source-path defect at `7ef0e07`: AnnotationWindow ignored a false result from
SetWindowDisplayAffinity, and its caller showed the window unconditionally.
This is a source inspection finding, not an executed native failure injection.

The repair initializes the annotation HWND before showing it, checks the
recorded exclusion result, and closes the still-hidden overlay on failure.
The owner stops active work, disables further capture for that panel lifetime,
and displays the limitation. Rendering returns before proposing any further
action from the invalidated generation.

Native failure injection and actual captured-frame exclusion remain pending;
neither compilation nor the deterministic suite proves those properties.
