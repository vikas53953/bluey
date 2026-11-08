# Toolchain

Pinned workspace SDK: .NET 10.0.403, Windows x64; runtime 10.0.14; Windows build 26200. Official ZIP SHA-512 verified against Microsoft release metadata. Source and hash are in ignored .tools/dotnet-sdk-source.json.

Use scripts/build.ps1; system dotnet lacks an SDK. Libraries expose nonfunctional contracts until independent RED tests exist. No fixture or harness build counts as production acceptance.

OpenAI models listing authenticated; paid inference and actual voice remain untested. Secrets are ignored and must never be logged, committed or included in agent packets.

