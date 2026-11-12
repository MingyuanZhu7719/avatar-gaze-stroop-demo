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
            Run("controller diagnostic freshness",ViveControllerUiFreshnessFixture.Validate);
            Run("view failure diagnostics",ViveViewFailureDiagnosticFixture.Validate);
            Run("pre-session choices",VivePreSessionChoiceFixture.Validate);
            Run("evidence panel isolation",ViveEvidencePanelIsolationFixture.Validate);
            Run("legacy recording presentation",ViveLegacyRecordingPresentationFixture.Validate);
            Run("legacy scene configuration",ViveLegacySceneConfigurationFixture.Validate);
            Run("safety menu presentation",ViveSafetyMenuPresentationFixture.Validate);
            Run("raw pump safety",ViveRawPumpSafetyFixture.Validate);
            Run("named automatic presentation",ViveNamedAutomaticPresentationFixture.Validate);
            Run("named no-avatar presentation",ViveNamedAutomaticPresentationFixture.ValidateNoAvatar);
            Run("named multi-block presentation",ViveNamedAutomaticPresentationFixture.ValidateMultiBlock);
            Debug.Log("VIVE_OWNER_UI_REGRESSION_SYNTHETIC_FIXTURE_PASSED: 15 suites; no physical headset acceptance.");
        }
    }
}
#endif
