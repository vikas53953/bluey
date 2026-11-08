# Control observation provider tests

This file records the independent provider-test slice at the pinned source
revision `bbbedee0cab8a43b8416413478a105260af85d54`.

`tests/Boo.Integration.Tests/ControlObservationProviderTests.cs` exercises only
the public `ResponsesClient.RespondAsync` and `HttpMessageHandler` transport
seams. The tests cover:

- the separate observed-controls `input_text` JSON object in the `user` input,
  exact property/value shape, absence from instructions, and exclusion of
  runtime IDs and rejected candidates;
- missing-frame, exact-context (display, target, frame dimensions, and capture
  time), and request-generation binding rejection before credentials or
  transport;
- exact observed ID/name matching for click/type proposals, including an
  exact provided name, ordinal case mismatches, observed-name filling, the
  type-value capability and ID requirement, coordinate-only click compatibility,
  and preservation of other proposal order;
- empty-observation suppression of ID-bearing and no-ID type proposals while
  retaining coordinate-only click behavior;
- the existing two-argument `RespondAsync` contract without observation
  filtering.

After Core implementation at `424153d`, root integrated the independent tests
unchanged and ran them before provider implementation. This is genuine provider
RED: 10 failed, 1 legacy compatibility test passed, no factory setup failures.
Failures show missing control JSON, missing context rejection, and unfiltered
action proposals. Canonical TRX:
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_11_32_net10.0.trx`.
Raw output: `artifacts/control-provider-red.log`.

The current Core factory is intentional compile-time scaffolding and throws
`NotImplementedException` until the independent Core tests record their RED
result. Therefore provider tests that construct a `ControlObservation` are
expected to be setup-blocked at this revision; that exception is not provider
RED evidence. Once the Core factory is implemented, run the focused Release
filter before releasing the provider builder and classify any remaining
failures as provider behavior.

Focused command:

```powershell
./scripts/build.ps1 -Action test -Configuration Release -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Filter FullyQualifiedName~ControlObservationProviderTests
```

Run evidence at this revision: Release compilation succeeded and the filter
executed eleven tests. Ten tests stopped during setup at the intentional Core
factory `NotImplementedException`; these are setup-blocked, not provider RED.
The no-observation two-argument compatibility test passed. No provider
behavior RED result is claimed until the Core factory is implemented.
