# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION03

## Status

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION03 — CLOSED**

All release-review blockers are resolved. The archive rollback is safe
for the ordinary failed-replacement-move case, the previous-temp is
preserved whenever recovery is incomplete, the launcher policy is
derived from the parsed manifest, the digest validator enforces strict
lowercase hexadecimal, and the committed tree reproduces `pass` for the
detached gate.

## Title

Harden `Archives.extractAtomicWith`, derive launcher equality from the
manifest, enforce strict digest validation, and attach the committed
gate evidence.

## Objective

Address the four R1 issues raised against CORRECTION02:

1. `extractAtomicWith` must bring `absoluteFinal` back to the previous
   install whenever the second `Move` throws before effect, and the
   `finally` block must not destroy the only recovery copy.
2. `LauncherPolicyTests.fs` must derive the expected image from the
   parsed manifest and prove that a manifest-image mutation breaks
   launcher equality.
3. `validate` must reject any `bootstrap_sdk_image.digest` whose
   payload is not lowercase hexadecimal.
4. The committed clean tree must regenerate a `pass` detached gate
   summary.

## Mandated order

1. Restored the `Archives.extractAtomicWith` state-driven rollback so
   that an `absoluteFinal` is never discarded while `previousDir` still
   holds the previous install.
2. Replaced the throwaway `Error` branch in `reportInstallFailure` with
   a `restorePrevious` call that mirrors the verification-failure path.
3. Added `LauncherPolicyTests.fs` cases that derive the expected
   `BOOTSTRAP_IMAGE='reference@sha256'` from the parsed manifest and
   assert that a mutation of the manifest's digest would break the
   launcher equality.
4. Strengthened `isPinnedSha256` to validate every payload character
   against the lowercase hex alphabet and added a negative test.
5. Re-ran the build, the Expecto suite, the launcher policy, and the
   detached gate chain against the committed tree.
6. Updated the CORRECTION02 ACT and close report, leaving
   CORRECTION01/CORRECTION02 historical records intact.

## Outcome summary

| claim | evidence |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj -c Release` | 0 warnings, 0 errors |
| Expecto suite | 29/29 tests passing |
| Archive rollback: failed replacement move | `Error`; `old.txt` is back in the final dir; `new.txt` is gone |
| Archive rollback: failed second-move that mutates then throws | `Error`; the previous install is live; the failed candidate is gone |
| Archive rollback: failed extraction | `Error`; the previous install is preserved |
| Archive rollback: failed verification | `Error`; the previous install is restored; the unverified candidate is gone |
| Archive rollback: cold-start failed second move | `Error`; the failed candidate is gone; the final dir is absent |
| Launcher policy: Python / `jq` absence | `clean` |
| Launcher policy: pinned image derived from manifest | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` matches the manifest |
| Launcher policy: manifest mutation breaks equality | mutation case passes; the launcher only pins the manifest value |
| Manifest validation: non-hex digest rejected | `validate` returns `Error` for `"sha256:???..."` |
| Detached gate | `pass (3/3 pass) tree=b0b8f4754b2a`; canonical vocabulary ok; tree-OID binding ok; leamas digest pass |
| `git status` post-build | working tree clean after regenerating the gate summary (which is `.gitignore`d) |
