# Issue78 offline evidence (14 Nov)

**manual — attribution remains inconclusive; no product fix or acceptance pass.**

Existing capture: https://github.com/vikas53953/bluey/actions/runs/43480914897 . App/harness7f8960aa83d2fbbac1f17be6f37c1cef85ad65af. ETL SHA256358066B3E650F9D325CD7646B804E0488E4929602BF6CEB72DA18E245CC59C53. No new capture/provider call during analysis.

TraceProcessing1.12.10 verified10MHz QPC reference2925230101; Show interval6.1952276–6.5672808s. BooPID6336,parent7340.325captured samples/315on nativeTID7336; recorded clipped7336scheduling318.9622ms. This is incomplete recorded evidence, not a complete wall-time accounting. Managedthread2 mapping remains UNVERIFIED.

Final ETW loss163432events despite pre-stop0; merged counters do not localize loss. UiTiming/profiler overhead confounded. First global UseSymbols pass failed in offline reader with OutOfMemoryException, not a Boo memory claim.

One bounded metadata-only pass (54.98s/1020448768private bytes) supplied address ranges/lifetimes/PDB identities.5168native frame occurrences matched four explicit images uniquely. Registration-free x64 DIA validated GUID+age for coreclr,clrjit,PresentationNative_cor3,ntdll PDBs retrieved only from Microsoft. Containing-function lookup resolved624/732deduplicated RVAs; all106ntdll RVAs stayed unresolved. This named4560additional frame occurrences;31043/36843still unnamed.

Inclusive JIT/PreStub stacks appear, but inclusive sample weights overlap and cannot establish an exclusive hotspot. Saved samples lack sampled-IP/TopStackFrame addresses, so no new exclusive native leaf weights were invented. No managed frames were interpreted through native PDB lookup. Missing events/stacks cannot establish absence of work.

Detailed raw exports, reader scripts, limits, PDB hashes and failures remain in local artifacts/etw-reader; the complete local report is artifacts/etw-reader/offline-report.md. Issue: https://github.com/vikas53953/bluey/issues/78 . All j10a assertions/limits, frozen oracle and no-retry requirements unchanged. Full-app NOT PASSED; remaining required coverage is recorded in PR54.
