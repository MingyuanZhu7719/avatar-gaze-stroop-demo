namespace AvatarStroop.Runtime
{
    // Availability for one explicitly built Windows XR development player.
    // This is not consent, SDK readiness, file authority, research acceptance,
    // hardware verification or evidence that a recording operation succeeded.
    // Every installed entry retains its exact-source and per-operation checks.
    // There is no runtime switch, persisted preference or caller override.
    internal static class ViveControlledRecordingBuildPolicy
    {
        internal static bool Available
        {
            get
            {
#if STROOP_CONTROLLED_RECORDING_DEVELOPMENT && STROOP_XR_PREVIEW && UNITY_5_3_OR_NEWER && UNITY_STANDALONE_WIN && DEVELOPMENT_BUILD && !UNITY_EDITOR && !UNITY_INCLUDE_TESTS && !STROOP_TEST_BUILD && !VIVE_RAW_SYNTHETIC_TEST && !VIVE_SYNTHETIC_BATCH_FIXTURE
                return true;
#else
                return false;
#endif
            }
        }
    }
}
