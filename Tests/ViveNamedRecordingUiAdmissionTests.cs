using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedRecordingUiAdmissionTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("v3 recording UI admits only explicit off intent while recorder unavailable",()=>{
                Check.True(!ViveNamedRecordingUiAdmission.RecordingAvailable);
                var intent=ViveNamedRecordingUiAdmission.Create(Actor.Owner,"synthetic-intent",false,false);
                Check.True(!intent.TaskRequested&&!intent.EyeRequested);
                foreach(var pair in new[]{new[]{true,false},new[]{false,true},new[]{true,true}})
                    Check.Throws<InvalidOperationException>(()=>ViveNamedRecordingUiAdmission.Create(Actor.Owner,"synthetic-intent",pair[0],pair[1]));
                Check.Throws<UnauthorizedAccessException>(()=>ViveNamedRecordingUiAdmission.Create(Actor.Participant,"synthetic-intent",false,false));
            });
        }
    }
}
