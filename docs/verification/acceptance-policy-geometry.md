# Core policy, lifecycle, and geometry acceptance cases

These cases freeze the public behavior boundaries for the first Boo.Core
builders. They are intentionally written against the public records and
methods in `src/Boo.Core/Contracts.cs`, `RequestCoordinator`, `ActionPolicy`,
`ScopedFiles`, `AnnotationValidator`, and `DisplayGeometry`.

The approved plan requires these tests to exist before feature behavior is
implemented. A failing result caused by a `NotImplementedException` is the
expected RED signal for the initial stubs. Missing SDK, compiler, project
reference, or test discovery is harness failure, not RED evidence.

## Request lifecycle cases

| ID | Observable case | Expected result | Executable test |
| --- | --- | --- | --- |
| RC-001 | Begin a second request after the first lease exists. | The old token is cancelled and old generation is not current; only the new generation is current. | `Begin_invalidates_the_prior_lease_and_cancels_its_token` |
| RC-002 | Stop a request, then submit its late commit. | Stop cancels the lease, marks its generation stale, rejects the commit, and runs no effect. | `Stop_rejects_a_late_generation_and_does_not_run_its_effect` |
| RC-003 | Commit a current generation before Stop. | The effect runs and the commit reports success; Stop then makes the generation stale. | `A_current_generation_can_commit_its_effect_before_stop` |
| RC-004 | Hold a current commit at a controlled gate while Stop is requested. | Stop and commit share a serialized boundary: the already-entered commit effect completes before Stop, with no post-Stop effect. | `Stop_and_commit_share_a_serialized_boundary_when_forced_to_overlap` |
| RC-005 | Dispose with an active lease, then call Begin. | Dispose behaves as Stop, cancels the lease, rejects late commit/currentness, and disallows a new Begin. | `Dispose_stops_the_current_generation_and_disallows_begin` |

RC-004 uses `ManualResetEventSlim` and task completion, not sleeps or an
unbounded race. The test releases the controlled effect only after the Stop
thread has reached the overlap point and bounds completion waits.

## Action authorization cases

The initial conservative allowlist is `search`, `inspect`, `open_url`,
`open_app`, `click`, `type_text`, `write_file`, and `delete_file`. The current
task authority must be explicit, current, and allowlist the tool. Approval is
bound to the immutable tool name, target, sorted argument keys/values, and
generation.

| ID | Observable case | Expected result | Executable test |
| --- | --- | --- | --- |
| AP-001 | Evaluate an unknown tool, even when its name is placed in the allowlist. | Denied. | `Unknown_tool_is_denied_even_when_the_model_lists_it_as_allowed` |
| AP-002 | Evaluate a read-only request without an explicit user task. | Denied. | `A_request_without_an_explicit_user_task_is_denied` |
| AP-003 | Evaluate a known tool omitted from the allowlist. | Denied. | `A_tool_not_in_the_current_allowlist_is_denied` |
| AP-004 | Evaluate an otherwise allowed request from an older generation. | Denied. | `A_stale_generation_is_denied` |
| AP-005 | Explicitly allowlist `search` or `inspect`. | Allowed without approval. | `Read_only_tools_are_allowed_only_with_explicit_authorization` |
| AP-006 | Explicitly allowlist an `http://` or `https://` URL open. | Allowed without approval. | `Explicitly_allowlisted_http_urls_are_read_only` |
| AP-007 | Open `file:`, `mailto:`, or another custom scheme. | Denied, including when an approval ticket is supplied. | `Non_http_url_schemes_are_denied_even_with_an_approval_ticket` |
| AP-008 | Evaluate click, text typing, file write (including overwrite), delete, or app launch. | Needs concrete approval, then Allowed only with the matching ticket. | `UI_impacting_and_other_unknown_impact_tools_need_concrete_approval` |
| AP-009 | Change the target after approval. | The old ticket is invalid and does not allow the changed request. | `Changing_the_target_invalidates_a_concrete_approval` |
| AP-010 | Change an argument after approval. | The old ticket is invalid and does not allow the changed request. | `Changing_arguments_invalidates_a_concrete_approval` |
| AP-011 | Reorder otherwise identical arguments after approval. | The ticket remains valid because argument digesting is sorted and deterministic. | `Reordering_arguments_does_not_invalidate_a_concrete_approval` |
| AP-012 | Change the request generation after approval. | The old ticket is invalid and does not allow the new generation. | `Changing_the_generation_invalidates_a_concrete_approval` |

## Scoped file cases

| ID | Observable case | Expected result | Executable test |
| --- | --- | --- | --- |
| SF-001 | Resolve a normal relative path below a selected root. | Return its canonical contained path. | `Contained_relative_paths_resolve_to_the_canonical_path` |
| SF-002 | Resolve traversal (`..`). | Reject the path. | `Traversal_is_rejected` |
| SF-003 | Resolve an absolute path. | Reject the path. | `Absolute_paths_are_rejected` |
| SF-004 | Resolve a sibling whose name only shares the root prefix. | Reject the path. | `Sibling_prefix_escapes_are_rejected` |
| SF-005 | Resolve an alternate data stream (`name:stream`). | Reject the path. | `Alternate_data_streams_are_rejected` |
| SF-006 | Resolve through a directory or file reparse point. | Reject the path. The test attempts to create a symbolic link without changing system settings; if the host denies link creation, native acceptance remains pending for a reparse-capable environment. | `Reparse_point_escapes_are_rejected_when_the_host_allows_a_fixture_link` |

All rejection assertions explicitly fail if the observed exception is the
initial `NotImplementedException`, so a stub cannot masquerade as a passing
security test.

## Annotation and display geometry cases

| ID | Observable case | Expected result | Executable test |
| --- | --- | --- | --- |
| AN-001 | Validate a proposal exactly 15 seconds old. | Valid (the accepted age interval is 0 through 15 seconds). | `A_proposal_at_the_fifteen_second_age_boundary_is_valid` |
| AN-002 | Validate a proposal older than 15 seconds. | Rejected. | `A_proposal_older_than_fifteen_seconds_is_rejected` |
| AN-003 | Validate a proposal captured in the future. | Rejected. | `A_proposal_from_the_future_is_rejected` |
| AN-004 | Change observation ID, generation, or display ID. | Rejected as mismatched context. | `Mismatched_observation_generation_and_display_are_rejected` |
| AN-005 | Validate against an empty/invalid target identity. | Rejected. | `An_empty_target_context_is_rejected` |
| AN-006 | Validate a proposal containing no marks. | Rejected because it has no target to annotate. | `A_proposal_without_any_marks_is_rejected` |
| AN-007 | Use a NaN mark coordinate. | Rejected. | `Non_finite_mark_coordinates_are_rejected` |
| AN-008 | Use negative or beyond-frame mark bounds. | Rejected. | `Marks_outside_the_captured_frame_are_rejected` |
| AN-009 | Use `ring`, `arrow`, or `label`. | Valid. | `The_documented_annotation_kinds_are_accepted` |
| AN-010 | Use an unknown mark kind. | Rejected. | `An_unknown_annotation_kind_is_rejected` |
| AN-011 | Map a 1280×720 frame rect to a 1920×1080 physical display at 150% with a negative origin. | `(100,50,200,100)` maps literally to `(-1770,-125,300,150)` physical pixels. | `Frame_coordinates_map_to_literal_physical_pixels_with_a_negative_origin_and_150_percent_scale` |
| DG-001 | Center a 200×80 DIP notch on a 1920×1080 physical display at 150% with 20 DIP top offset. | Return `(-1110,-170,300,120)` physical pixels. | `Notch_bounds_are_centered_and_returned_in_physical_pixels` |

## Execution record

Run the focused suite through the canonical wrapper once root has completed the
test project and SDK bootstrap:

```powershell
.\scripts\build.ps1 test tests/Boo.Core.Tests
```

Record the exact wrapper/SDK, test discovery, and failure output in the root
RED-baseline evidence. Do not count compiler/tooling failures as product RED.
