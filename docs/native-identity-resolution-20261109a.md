Historical version scope: This note describes the closed-gate recording candidate. Its gate-state and validation statements are historical, not the policy of the newer optional controlled Development version. See controlled-recording-development-evidence-20261109b.md and controlled-recording-public-manifest-20261109b.json for the newer version and its untested hardware boundaries.

# Native file identity: correction and evidence limits

Date: 2026-11-09. This is a sanitized historical summary, not a public-tree native test result.

## Correction

The earlier witness-open probe passed zero DesiredAccess to NtCreateFile. Microsoft documents that zero is invalid for this API, although the higher-level CreateFileW supports zero for some metadata queries. The corrected witness requests FILE_READ_ATTRIBUTES (0x80), retains the original writer's exclusive data-share mode, and uses READ|WRITE|DELETE sharing only for the metadata witness. No security or permission setting was changed.

Sources:
- [NtCreateFile](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile#remarks)
- [File access rights](https://learn.microsoft.com/en-us/windows/win32/fileio/file-access-rights-constants)
- [Metadata-only sharing](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/granting-oplocks#using-the-legacy-filter-oplock)
- [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)

A historical controlled metadata-only probe succeeded with the corrected access value. This establishes an API-contract defect and a successful corrected run, not the cause of every possible access denial.

## Integrated lifecycle

One shared state machine performs 12 explicit metadata operations around the separate original data-handle close: directory identity, witness open/type/ID/attributes, directory identity after close, reopen/type/ID/attributes, reopen close and witness close. Each operation uses a fresh existing revocable claim. Pending, unknown or mismatched steps stop permanently. Directory cleanup and output receipts cannot complete until the relevant witness lifecycle completes.

Identity means matching the pinned volume and file identifier at the completed check. It does not guarantee a future absolute-path lookup, participant authorization, publication permission or durable media persistence.

Eight historical synthetic native scenarios checked the successful lifecycle, same-name replacement, different expected directory identity, missing original close, missing witness file, two injected pre-close failures, and withdrawal before reopen. The two injected failures were not actual operating-system CloseHandle failures. Unknown cases retained unresolved resources until process exit and do not establish successful cleanup.

## Public reproducibility boundary

The native fixtures, diagnostic outputs and machine-bound scripts are omitted. Both legacy native allowlists are disabled and the integration fixture entry fails closed before filesystem access. No native run was performed on the sanitized public source. Historical outputs and managed-assembly hashes are deliberately not represented as public-tree results.

See recording-public-manifest-20261109a.json for the new managed-only validation scope. All six production gates remain false. Hardware/SDK operation, a recording-capable player, real-data reconciliation and storage durability remain unvalidated.
