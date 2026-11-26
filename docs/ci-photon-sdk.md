# Photon SDK in GitHub Actions

The application compiles against Photon Realtime Unity SDK **5.1.21**. The official download requires a Photon login; a fresh hosted runner cannot use the local developer's imported `Assets/Photon` directory.

The workflow restores `.github/dependencies/photon-realtime-5.1.21.aesgcm` using the repository Actions secret **`PHOTON_SDK_CI_KEY`**, then invokes the existing SDK importer and verifier. The bundle contains only the official pinned SDK archive and its OSS notices. It contains no app configuration, credentials, participant data or application build. Decrypted vendor sources remain ignored by Git and are not uploaded as CI artifacts or caches.

GitHub documents the [encrypted large-file plus Actions secret pattern](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets#storing-large-secrets). This repository uses authenticated AES-256-GCM through PowerShell 7 on both hosted operating systems. The secret is supplied only to the provisioning steps, through their environment; it is never a command-line argument. Temporary plaintext archives are deleted on success and failure. Official archive/OSS SHA256 pins and the imported-file manifest remain mandatory after decryption.

## Trusted runs and forks

Pushes, manually dispatched runs, and same-repository pull requests require this repository secret. GitHub does not provide repository secrets to fork pull requests (or ordinary Dependabot runs). Those runs fail explicitly at provisioning with instructions; they do not report skipped compilation as a passing regression. A maintainer must review the entire proposed change, including workflow/build scripts, before testing it on a trusted branch. Never switch this workflow to `pull_request_target` to execute untrusted changes with the secret.

Local contributors can still log in to Photon and use the [ordinary SDK import procedure](woz-easy-pairing.md#开发与打包); they do not need the CI key. Synthetic tests make no live Photon connection and need no Photon App ID.

## Replacing the encrypted bundle

Use PowerShell 7 and Python. Obtain the official archive and OSS notices through the documented download process. Generate a new random key in the current shell, without displaying it:

```powershell
$env:PHOTON_SDK_CI_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
./scripts/seal-photon-ci.ps1 -ArchivePath <official-sdk.zip> -OssNoticesPath <official-oss.pdf> -OutputPath <new-bundle.aesgcm>
./scripts/test-restore-photon-ci.ps1 -BundlePath <new-bundle.aesgcm>
```

Set the repository Actions secret to that key using an authenticated GitHub client. With GitHub CLI, pass the value through standard input, not a command-line argument:

```powershell
$env:PHOTON_SDK_CI_KEY | gh secret set PHOTON_SDK_CI_KEY --repo MingyuanZhu7719/avatar-gaze-stroop-demo
Remove-Item Env:PHOTON_SDK_CI_KEY
```

Replace the encrypted bundle and push the corresponding workflow/source revision immediately after setting the matching secret; runs using an old bundle with a rotated key will fail authentication. Confirm every matrix job, including Windows Framework tests, passes. Do not commit the key or a plaintext SDK archive. Updating the vendor version also requires reviewing and updating the import pins/manifest and dependency path.

Bundle format: the 16-byte UTF-8 header `PHOTON-SDK-CI-1\n` (also authenticated additional data), a 12-byte nonce, a 16-byte authentication tag, then ciphertext. Plaintext is a ZIP with exactly `photon-unity-sdk_v5-1-21.zip` and `oss-.net_client_sdks.pdf`; arbitrary archive paths and additional entries are rejected. A new cryptographic nonce is generated for each sealing operation.
