#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Batch entry point for existing isolated synthetic fixtures. Stops on the
    // first failure; no result here represents physical hardware acceptance.
    public static class ViveOwnerUiRegressionFixture
    {
        static void Run(string name,Action validate)
        {
            Debug.Log("VIVE_OWNER_UI_REGRESSION_BEGIN: "+name);
            validate();
            Debug.Log("VIVE_OWNER_UI_REGRESSION_PASS: "+name);
        }
        public static void Validate()
        {
            Run("owner flow",ViveOwnerFlowFixture.Validate);
            Run("operator permissions and flow",ViveOperatorOwnerUiFixture.Validate);
            Run("single controller and neutral release",ViveSingleControllerFixture.Validate);
            Run("automatic controller discovery and commitment",ViveAutomaticControllerInputFixture.Validate);
            Run("selected controller pose snapshot",ViveControllerPoseAdapterFixture.Validate);
            Run("tracked wand model and pose",ViveTrackedWandVisualFixture.Validate);
            Run("tracked wand phase isolation",ViveControllerVisualPolicyFixture.Validate);
            Run("automatic eye service lifecycle",ViveAutoEyeViewStartFixture.Validate);
            Run("consent-driven recording and first Practice",ViveConsentAutoStartFixture.Validate);
            Run("explicit formal plan confirmation",ViveFormalConfirmationFixture.Validate);
            Run("fresh session storage",ViveSessionStorageFixture.Validate);
            Run("actionable setup before session storage",ViveSessionSetupValidationFixture.Validate);
            Run("responsive desktop geometry",ViveResponsiveOwnerLayoutFixture.Validate);
            Run("controller diagnostic freshness",ViveControllerUiFreshnessFixture.Validate);
            Run("recording focus policy",ViveRecordingFocusFixture.Validate);
            Run("recording start readiness",ViveRecordingReadinessUiFixture.Validate);
            Run("asynchronous recording start input/claim",ViveRecordingStartReadinessFixture.Validate);
            Run("view failure diagnostics",ViveViewFailureDiagnosticFixture.Validate);
            Run("pre-session choices",VivePreSessionChoiceFixture.Validate);
            Run("evidence panel isolation",ViveEvidencePanelIsolationFixture.Validate);
            Run("legacy recording presentation",ViveLegacyRecordingPresentationFixture.Validate);
            Run("recorded automatic avatar",ViveRawAutomaticAvatarFixture.Validate);
            Run("legacy scene configuration",ViveLegacySceneConfigurationFixture.Validate);
            Run("safety menu presentation",ViveSafetyMenuPresentationFixture.Validate);
            Run("raw pump safety",ViveRawPumpSafetyFixture.Validate);
            Run("Preparation render epoch race",VivePreparationPanelEpochFixture.Validate);
            Run("neutral raw hold view",ViveRawHoldViewFixture.Validate);
            Run("named automatic presentation",ViveNamedAutomaticPresentationFixture.Validate);
            Run("named no-avatar presentation",ViveNamedAutomaticPresentationFixture.ValidateNoAvatar);
            Run("named multi-block presentation",ViveNamedAutomaticPresentationFixture.ValidateMultiBlock);
            Debug.Log("VIVE_OWNER_UI_REGRESSION_SYNTHETIC_FIXTURE_PASSED: 30 suites; no physical headset acceptance.");
        }
    }
}
#endif
