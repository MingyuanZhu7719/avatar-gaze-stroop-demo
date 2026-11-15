#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Runtime;
using UnityEngine;
namespace AvatarStroop.Unity.Editor
{
    public static class ViveRecordingReadinessUiFixture
    {
        static void Require(bool value,string label){if(!value)throw new InvalidOperationException("Recording preflight UI: "+label);}
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var flags=BindingFlags.Static|BindingFlags.NonPublic;
            var describe=typeof(ViveStroopPreviewRig).GetMethod("DescribeRawStartReadiness",flags);
            Require(describe!=null,"specific preflight messages missing");
            var worker=typeof(ViveProEyeWorker);
            var snapshot=worker.GetNestedType("RecordingStartReadiness",BindingFlags.NonPublic);
            var states=worker.GetNestedType("RecordingStartReadinessState",BindingFlags.NonPublic);
            var reasons=worker.GetNestedType("RecordingStartReadinessReason",BindingFlags.NonPublic);
            foreach(var pair in new[]{new[]{"CalibrationRequired","calibration required"},new[]{"DirectionUnavailable","Eye direction is invalid"},
                new[]{"Stabilizing","0.5 seconds"},new[]{"Stale","stale or discontinuous"},new[]{"NativeStatusInvalid","runtime or read status"},
                new[]{"LockBusy","current eye-client check"}})
            {
                var value=Activator.CreateInstance(snapshot,BindingFlags.Instance|BindingFlags.NonPublic,null,
                    new object[]{Enum.Parse(states,"Waiting"),Enum.Parse(reasons,pair[0]),null},null);
                var text=(string)describe.Invoke(null,new[]{value});
                Require(text.IndexOf(pair[1],StringComparison.OrdinalIgnoreCase)>=0&&text.IndexOf("Recording has not started",StringComparison.OrdinalIgnoreCase)>=0,"waiting state omitted cause or recording-off distinction: "+pair[0]);
            }
            var ready=Activator.CreateInstance(snapshot,BindingFlags.Instance|BindingFlags.NonPublic,null,
                new object[]{Enum.Parse(states,"Ready"),Enum.Parse(reasons,"None"),null},null);
            Require(((string)describe.Invoke(null,new[]{ready})).Contains("head and controller checks are still required"),"ready metadata falsely became task permission");
            Require(((string)describe.Invoke(null,new object[]{null})).Contains("Enable eye tracking"),"absent worker was not explained");
            Debug.Log("VIVE_RECORDING_READINESS_UI_FIXTURE_PASSED: explicit preflight causes, off/ready distinction; no writer or XR.");
        }
    }
}
#endif
