using System;
using System.Reflection;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveControlledRecordingBuildPolicyTests
    {
        // Ordinary regression assemblies have STROOP_TEST_BUILD and must
        // remain closed even if a development define is added accidentally.
        public static void Register(TestSuite suite)
        {
            suite.Run("recording development policy remains closed in regression builds",()=>{
                Check.True(!ViveControlledRecordingBuildPolicy.Available);
                Check.True(!ViveNamedRecordingUiAdmission.RecordingAvailable);
                Check.True(!ViveRawRelativeFileCapability.ProductionGateOpen);
                Check.True(!ViveRawExclusiveDestination.ProductionGateOpen);
                Check.True(!ViveRawReadableAnalysisCsv.ProductionGateOpen);
            });
            suite.Run("recording development policy exposes no mutable or public override",()=>{
                var type=typeof(ViveControlledRecordingBuildPolicy);
                Check.True(!type.IsPublic);
                var members=BindingFlags.DeclaredOnly|BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance;
                Check.Equal(0,type.GetMembers(members).Length);
                var all=BindingFlags.DeclaredOnly|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
                Check.Equal(0,type.GetFields(all).Length);
                foreach(var property in type.GetProperties(all))Check.True(property.GetSetMethod(true)==null);
                Check.Equal(0,type.GetConstructors(all).Length);
            });
            suite.Run("closed recording build rejects preparation before directory inspection",()=>{
                bool inspected=false;string reason;
                var setup=ViveNamedRecordingSetup.TryPrepare(ViveControlledRecordingBuildPolicy.Available,
                    @"C:\NeverOpenedPolicyFixture","128",_=>{inspected=true;return true;},out reason);
                Check.True(setup==null&&!inspected&&!String.IsNullOrEmpty(reason));
            });
            suite.Run("recording development policy cannot create an unbound installed output",()=>{
                ViveRawExclusiveDestination result;
                Check.True(!ViveRawExclusiveDestination.TryCreateInstalled(out result)&&result==null);
                ViveNamedRawRecordingPump pump;
                Check.True(!ViveNamedRawRecordingPump.TryStartInstalled(null,null,null,null,null,out pump)&&pump==null);
            });
        }
    }
}
