# Candidate14 package identity

Development candidate; native acceptance is not complete.

- Source: `1493d18fb1133a645ee3f6ee0110ddaedf863453`
- Directory: `C:\Users\vikasmit\Downloads\blue--fo\artifacts\candidate14`
- Package: `artifacts/Boo-0.1.0-candidate14-win-x64.zip`
- ZIP SHA-256: `D96A8FBCD32985EEE7AEA529C919FD3825750D31CFF84812AB08DBDB488726A0`
- Manifest: `artifacts/candidate-14-manifest.json`
- Manifest SHA-256: `2A253248EA8ECAFE98436343E9B0FB623EE29049AE99920D3683BBD76B3FC70F`
- Boo.exe SHA-256: `5E896AE774EBF582B17BDBF2C6C18A7F23526019B9088127FDDED42D02C8793E`
- Boo.dll SHA-256: `4D177932C60150C4618B8545A0D1C0780233BF0CF58FBF2DFB79DC80A7C2F53D`
- Manifest file count: 496. The package contains only the self-contained publish output, not test profiles or credentials.

The exact source passed 242 Release tests with one unchanged host-dependent skip and an app build with zero warnings/errors. Oracle revision 16 verifies 43 frozen files. Formatting for the three repaired source files passed; the repository-wide formatting gate still has the previously recorded baseline failures in three other files.

A fresh reviewer traced the four source repairs and found no deterministic regression. Full artifact: `artifacts/reviews/shell-repair-1493d18.json`. Async capture cleanup, monitor behavior and all remaining native cases still need real observation. No browser route is affected by these native WPF changes; browser testing is not a substitute for that work.
