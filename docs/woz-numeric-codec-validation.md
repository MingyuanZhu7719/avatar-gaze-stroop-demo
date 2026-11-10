# WoZ numeric codec repair validation (2026-11-10)

## Verified mechanism and original incident boundary

One original aggregate memory test run reported `task_send_failed` during its first task publication (120 passed, 1 failed). The original log did not capture the inner transport reason or timestamp. Subsequent unchanged runs passed; that alone did not explain or resolve the failure.

A separate bounded diagnostic against the preserved original assembly deterministically demonstrated a production defect that can cause the same rejection path. On .NET Framework 4 x64, `3245 / 10000000.0` has bits `3F354434E3369B9D`. Formatting it with `R` yields `0.0003245`; parsing that text produces adjacent bits `3F354434E3369B9E`, whose R spelling is `0.00032450000000000003`. The original authenticated endpoint consequently rejects its own packet with `payload_not_canonical`; participant publication maps a failed send to `task_send_failed`.

A single bounded scan of 2,000,000 synthetic tick values found 58 noncanonical R round-trips and zero G17 bit mismatches. The original assembly hash was unchanged before and after. This establishes and repairs a mechanism capable of this failure, not proof that the unlogged value in the original incident was one of these values.

Microsoft documents the .NET Framework R-format limitation and the G17 workaround: https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-numeric-format-strings#round-trip-format-specifier-r

## Repair and compatibility

- `WireCodec.Number` keeps nonzero R spellings only when invariant parsing preserves the original double bits. Otherwise it uses G17 and verifies those bits too. If neither succeeds, encoding throws and fails closed.
- Both signed zeros explicitly normalize to `0`. Nonzero bit-safe R spellings remain unchanged. This is not a claim that every packet accepted by the old codec retains identical bytes.
- Canonical-byte equality, MAC validation, replay counters, freshness, peer identity, packet size, ownership, recording consent and recording gates are unchanged.
- Wire v2 remains restricted to the **same serialization runtime and build on both machines**. Do not mix old and repaired builds. A changed spelling may be rejected by the other build; no mixed-build or cross-runtime interoperability is claimed.
- The scene test logs the fixed transport issue only on failure. Successful execution performs no diagnostic reflection, and logs do not include packets, keys or participant identity.

## Verification

Production basis: PR #5 at `6923aa84707ec738ec19cedc67876c8e17a67990`, with an isolated two-production-file patch. The accompanying production manifest lists the exact 217 compiled source hashes. Public fixture allowlists remain inert. No frozen source, existing player or real data was modified.

| Check | Result |
| --- | --- |
| New authentication regressions against original production | 23 passed, 2 failed at the demonstrated bit mismatch; control suite 10 passed |
| Same authentication tests with repair | 25 passed, 0 failed; control suite 10 passed |
| Repaired aggregate memory flow, one original-order run | 121 passed, 0 failed, including scene 11/11 |
| Ordinary preview Unity API compilation | 217 production sources, exit 0, no diagnostics |
| Controlled legacy API compilation | 217 production sources, exit 0, no diagnostics |
| Controlled legacy WoZ API compilation | 217 production sources, exit 0, no diagnostics |

Each managed test compile records 202 source inputs. Each API compile records 406 source/reference/additional inputs. Their hashes remained unchanged throughout validation. The regression suite covers the failing values and neighbors, a fixed 65,536-value synthetic tick grid, task context, TimeReply fields, positive/negative tracking components, signed zero, finite extremes, exact re-encoding, golden packets, malformed canonical text, byte tampering and replay rejection. The exact positive/negative comparison used native .NET Framework x64; it is not a Unity player execution.

The normal portable CI entry must invoke `WozAuthenticatedLanTests.RunAll()` and `WozAuthenticatedControlTests.RunAll()` so these regressions continue to run. The LAN method returns 0/1; the control method returns a failure count.

## Limits

This repair has not rebuilt the existing participant/operator player packages. Those packages do not contain this source fix. No new native storage run, SDK session, physical LAN exchange, HMD test or real participant recording was performed. A repaired player pair must be built from the same reviewed source before hardware acceptance. All test observations were synthetic.

No cryptographic or system-security settings were changed. The unencrypted LAN's established privacy limits still apply. This validation does not grant participant-recording consent or establish hardware/clock accuracy.

## Evidence hashes

- Original failed assembly: `13C1203708E5E12382E215AF9D4C6213F0E103183BC33A2C3E74A2FAAC19C47F`.
- Bounded mechanism diagnostic log: `7837767DA3729E6F0BA8BEC18FEAADBE34A0B06AC484E323A0CA3B7C2933EEF3`.
- Original-production regression log: `AD333DDE4FDCA49EC108D5F4046A062C9D00A2A16EBCB7749A786986B03A19AD`.
- Repaired authentication regression log: `ADB5A7A43BAFA07FD7BEFE1B982A7E7F8694FFC1442AC8767756322160E06533`.
- Repaired memory aggregate log: `7BD66D82A7E9271BFD9F03BF7395DA4CDBE657417D4318BE7FF83A40D1DF645F`.
- Repaired managed regression assembly: `EAC33C9A51932069A0C1B37B74E06B5487956C1CAC1889050BC4B49316F06096`.
- Production manifest: `D4C37E05EB870CFC7E800DFAA7B033A5EAB32302A4BFFC8EB8E7329F8E3558D3`.

Raw local logs, machine paths and installed vendor dependencies are not published. The reproducible regression cases are in `Tests/WozAuthenticatedLanTests.cs`; API compilation is evidence against existing local Unity/XR references, not a clean public-tree player build.
