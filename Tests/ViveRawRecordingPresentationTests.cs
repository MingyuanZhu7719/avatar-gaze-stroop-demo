using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRawRecordingPresentationTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("raw recording owner status represents every software state without claiming validity",()=>{
                foreach(ViveNamedRawRecordingPump.Outcome state in Enum.GetValues(typeof(ViveNamedRawRecordingPump.Outcome)))
                {
                    string text=ViveRawRecordingPresentation.Describe(false,false,false,state);
                    Check.True(!String.IsNullOrWhiteSpace(text),"missing status for "+state);
                    Check.True(!text.Contains("saved successfully")&&!text.Contains("complete data"),"unsupported success for "+state);
                    if(state==ViveNamedRawRecordingPump.Outcome.CloseCallsReturned)
                        Check.True(text.Contains("not confirmed")&&text.Contains("close calls returned"));
                    if(state==ViveNamedRawRecordingPump.Outcome.IoUncertain)
                        Check.True(text.Contains("unconfirmed")&&text.Contains("unknown"));
                }
            });
            suite.Run("raw recording owner status keeps withdrawal distinct from technical failure",()=>{
                string withdrawal=ViveRawRecordingPresentation.Describe(true,true,true,ViveNamedRawRecordingPump.Outcome.Faulted);
                string fault=ViveRawRecordingPresentation.Describe(true,false,true,ViveNamedRawRecordingPump.Outcome.Faulted);
                Check.True(withdrawal.Contains("Withdrawal requested")&&!withdrawal.Contains("could not start"));
                Check.True(fault.Contains("could not start")&&!fault.Contains("Withdrawal"));
            });
            suite.Run("raw recording owner status does not show running after safety is latched",()=>{
                string status=ViveRawRecordingPresentation.Describe(false,false,true,ViveNamedRawRecordingPump.Outcome.Running);
                Check.True(status.Contains("safety stop")&&!status.Contains("running")&&status.Contains("unknown"));
                string finishing=ViveRawRecordingPresentation.Describe(false,false,false,ViveNamedRawRecordingPump.Outcome.Finishing);
                Check.True(finishing.Contains("finishing")&&!finishing.Contains("safety stop"));
            });
        }
    }
}
