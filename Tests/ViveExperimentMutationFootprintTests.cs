using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Enumerates one synthetic development configuration and in-memory VRAW3
    // encodings. Observed maxima are not universal reservations, device timing,
    // proof of a successful write, or protection against future schema changes.
    public static class ViveExperimentMutationFootprintTests
    {
        sealed class Clock : IClock {public double Now {get;set;} public string ClockId {get{return "footprint-clock";}}}
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();
            internal readonly Experiment Task;
            internal readonly ViveNamedSessionFrameAdmission.Manifest Manifest;
            // Count expectations are deliberately tied to this schema and
            // measured branches; a passing test does not bound all operations.
            static readonly Dictionary<string,int> ExpectedCounts=new Dictionary<string,int>{
                {"familiarization",1},{"practice_plan",1},{"practice_start",3},{"present",1},
                {"practice_correct_next",3},{"practice_wrong_correction",2},{"practice_correction_continue",3},
                {"practice_pause",3},{"practice_resume",4},{"practice_final_correct",3},
                {"confirm_plan",4},{"main_plan",1},{"main_countdown",1},{"main_first_tick",2},
                {"main_correct_next",2},{"main_pause",3},{"main_resume",2},{"main_resume_tick",2},{"main_final_response",4}};
            internal Fixture()
            {
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"footprint-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"footprint-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"footprint-intent",true,true);
                Task=new Experiment(SessionConfiguration.NamedRecordingDevelopment("footprint-session",37,plan,intent),Clock);
                Manifest=new ViveNamedSessionFrameAdmission.Manifest("footprint-session",Clock.ClockId,plan,intent,true);
                Manifest.SealChoiceTime(0,-1);
            }
            internal int Mutation(string label,Action action)
            {
                int before=Task.Events.Count;Phase phase=Task.Phase;action();int after=Task.Events.Count;
                Check.Equal(ExpectedCounts[label],after-before,"event delta changed for "+label);
                int bytes=0;var parts=new List<string>();
                for(int i=before;i<after;i++)
                {
                    var item=Task.Events[i];Check.Equal((long)i,item.Sequence);
                    Check.Equal("footprint-session",item.SessionId);Check.Equal(Clock.ClockId,item.ClockId);
                    byte[] digest,payload=ViveRawSessionJournalCodec.RecordPayload(i+1,item,Manifest);
                    byte[] frame=ViveRawSessionJournalCodec.FrameV3(payload,new byte[32],false,out digest);
                    bytes+=frame.Length;
                    parts.Add(item.Kind+"[b="+item.BlockIndex+",s="+item.Segment+",trial="+(item.TrialId??"none")+",bytes="+frame.Length+"]");
                }
                Console.WriteLine("FOOTPRINT "+label+" "+phase+"->"+Task.Phase+" events="+(after-before)+" encoded_frame_bytes="+bytes+" "+String.Join(";",parts));
                return after-before;
            }
            internal void Present(){Check.Equal(1,Mutation("present",()=>Task.MarkPresented(Task.CurrentTrial.TrialId)));}
            internal void Answer(bool correct,string label)
            {
                var ink=Task.CurrentTrial.Stimulus.Ink;var answer=correct?ink:(Colour)(((int)ink+1)%4);
                Clock.Now+=.2;Mutation(label,()=>Check.True(Task.ButtonChanged(answer,true)));Task.ButtonChanged(answer,false);
            }
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic Experiment mutation event footprints expose actual cursor deltas",()=>{
                var f=new Fixture();var e=f.Task;
                f.Mutation("familiarization",()=>e.BeginFamiliarization(Actor.Owner));
                Check.Equal(1,f.Mutation("practice_plan",()=>e.PrepareNamedRecordingBlockPlan(Actor.Owner,true)));
                f.Mutation("practice_start",()=>e.BeginPractice(Actor.Owner));
                f.Present();f.Answer(true,"practice_correct_next");
                f.Present();f.Answer(false,"practice_wrong_correction");
                Check.Equal(Phase.PracticeCorrection,e.Phase);
                f.Mutation("practice_correction_continue",()=>e.ContinuePractice());
                f.Present();f.Mutation("practice_pause",()=>e.Pause(Actor.Participant));
                f.Clock.Now+=.3;f.Mutation("practice_resume",()=>e.Resume(true));
                while(e.Phase==Phase.Practice)
                {int index=e.CurrentTrial.PlannedIndex;f.Present();f.Answer(true,index==7?"practice_final_correct":"practice_correct_next");}
                Check.Equal(Phase.BlockPreparation,e.Phase);
                f.Mutation("confirm_plan",()=>e.ConfirmNamedPlan(Actor.Owner,"synthetic-owner"));
                Check.Equal(1,f.Mutation("main_plan",()=>e.PrepareNamedRecordingBlockPlan(Actor.Owner,false)));
                f.Mutation("main_countdown",()=>e.BeginCountdown(Actor.Owner,true));
                f.Clock.Now+=3;f.Mutation("main_first_tick",()=>e.Tick());
                f.Present();f.Answer(true,"main_correct_next");
                f.Present();f.Mutation("main_pause",()=>e.Pause(Actor.Participant));
                f.Clock.Now+=.3;f.Mutation("main_resume",()=>e.Resume(true));
                f.Clock.Now+=3;f.Mutation("main_resume_tick",()=>e.Tick());
                while(e.Phase==Phase.MainTask)
                {int index=e.CurrentTrial.PlannedIndex;f.Present();f.Answer(true,index==23?"main_final_response":"main_correct_next");}
                Check.Equal(Phase.Questionnaire,e.Phase);
            });
            suite.Run("preparation user text is not bounded by event production and codec may reject it",()=>{
                var f=new Fixture();f.Task.BeginFamiliarization(Actor.Owner);int before=f.Task.Events.Count;
                f.Task.RecordPreparation(Actor.Owner,"synthetic-field",new String('\u754c',11000));
                Check.Equal(before+1,f.Task.Events.Count);var item=f.Task.Events[before];
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(before+1,item,f.Manifest));
                Console.WriteLine("FOOTPRINT unbounded_record_preparation field_chars=11000 utf8_bytes=33000 event_added=true codec_text_limit=32768 rejected=true");
            });
            suite.Run("active trial technical strings can exceed codec after state mutation",()=>{
                var flagged=new Fixture();var a=flagged.Task;a.BeginFamiliarization(Actor.Owner);a.BeginPractice(Actor.Owner);
                a.MarkPresented(a.CurrentTrial.TrialId);int prior=a.Events.Count;
                a.FlagTechnicalIssue(new String('x',32769));Check.Equal(prior+1,a.Events.Count);
                Check.True(a.CurrentTrial.TechnicalIssue);
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(prior+1,a.Events[prior],flagged.Manifest));
                var stopped=new Fixture();var b=stopped.Task;b.BeginFamiliarization(Actor.Owner);b.BeginPractice(Actor.Owner);
                b.MarkPresented(b.CurrentTrial.TrialId);prior=b.Events.Count;
                b.EndTechnical(Actor.Owner,new String('y',32769));
                Check.Equal(Phase.Ended,b.Phase);Check.Equal(prior+3,b.Events.Count);
                Check.Equal("trial_interrupted",b.Events[prior].Kind);Check.Equal("session_ended",b.Events[prior+1].Kind);
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(prior+1,b.Events[prior],stopped.Manifest));
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(prior+2,b.Events[prior+1],stopped.Manifest));
                Console.WriteLine("FOOTPRINT active_technical code_or_reason_utf8_bytes=32769 event_added=true codec_text_limit=32768 rejected=true");
            });
        }
    }
}
