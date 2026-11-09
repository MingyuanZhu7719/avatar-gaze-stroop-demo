#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Runs the real blocked Rig.Update and owner handlers with a synthetic
    // in-memory pump/client only. No XR, vendor client, network or file I/O.
    public static class ViveRawPumpSafetyFixture
    {
        sealed class SyntheticClient:IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic Editor fixture";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Raw pump fixture: "+message);}
        static FieldInfo Field(object target,string name)
        {var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);if(field==null)throw new InvalidOperationException("Missing field: "+name);return field;}
        static object Get(object target,string name){return Field(target,name).GetValue(target);}
        static void Set(object target,string name,object value){Field(target,name).SetValue(target,value);}
        static object Call(object target,string name,params object[] args)
        {var method=target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic);if(method==null)throw new InvalidOperationException("Missing method: "+name);return method.Invoke(target,args);}
        static GameObject Root(string name,Scene scene)
        {var result=new GameObject(name);SceneManager.MoveGameObjectToScene(result,scene);Require(result.scene==scene,"invalid fixture scene");result.hideFlags=HideFlags.HideAndDontSave;return result;}
        static void Hold(object gate,Action whileHeld)
        {
            using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            {
                var thread=new Thread(()=>{lock(gate){entered.Set();release.WaitOne();}}){IsBackground=true};thread.Start();
                try{Require(entered.WaitOne(3000),"synthetic lock was not acquired");whileHeld();}
                finally{release.Set();Require(thread.Join(3000),"synthetic lock holder did not stop");}
            }
        }
        static ViveDevelopmentParticipant Task(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"raw-fixture-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-raw-fixture-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-raw-fixture-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-raw-fixture-session",42,plan,intent);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"raw-fixture-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        static void VerifyUnavailableBeforeTask(Scene scene,Camera camera,WorldSpaceExperimentView view,string mode)
        {
            GameObject rigObject=null,bootstrapObject=null;ViveStroopPreviewRig rig=null;
            ViveStroopDevelopmentBootstrap bootstrap=null;ViveProEyeWorker worker=null,unstarted=null;
            SyntheticClient client=null;object pump=null;
            try
            {
                rigObject=Root("SyntheticUnavailableRig-"+mode,scene);rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=Root("SyntheticUnavailableBootstrap-"+mode,scene);bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-unavailable-"+mode);client=new SyntheticClient();
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"synthetic unavailable worker setup failed");
                var task=Task(clock);var admissionType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true);
                var admission=admissionType.GetMethod("CreateSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,32});
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"unavailable fixture choices failed");
                bool rejected=false;try{task.OwnerBeginPractice(Actor.Owner);}catch(InvalidOperationException){rejected=true;}
                Require(rejected&&task.Phase==Phase.Preparation&&task.CurrentTrial==null,
                    "recording-attached practice must not begin without a plan receipt");
                // Fixture-only setup deliberately bypasses the new start gate
                // to exercise failures on an already-active synthetic trial;
                // this is not a receipt or a production start path.
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                if(mode!="stale-menu")Call(task,"OwnerBeginPracticeCore",Actor.Owner);var trial=task.CurrentTrial;
                var pumpType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writerType=pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic);
                var writer=Activator.CreateInstance(writerType,BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{admission},null);
                pump=pumpType.GetMethod("StartSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,admission,writer});
                Set(bootstrap,"participant",task);Set(bootstrap,"sharedClock",clock);Set(bootstrap,"configured",true);Set(bootstrap,"namedRawPump",pump);
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",bootstrap);Set(rig,"running",true);camera.cullingMask=-1;
                if(mode=="stale-menu")
                {
                    Call(bootstrap,"HandleParticipantMenu");
                    Require(!(bool)Get(bootstrap,"rawSafetyLatched")&&Get(bootstrap,"rawSafetyCause").ToString()=="TechnicalFault",
                        "menu in preparation must not leave a participant cause");
                    Call(task,"OwnerBeginPracticeCore",Actor.Owner);trial=task.CurrentTrial;
                }
                var staleStimulus=(TextMesh)Get(view,"stimulusText");staleStimulus.gameObject.SetActive(true);
                if(mode=="stopping"){Set(rig,"worker",worker);Set(rig,"stopping",true);}
                else if(mode=="render"||mode=="open"||mode=="stop-eye-busy")Set(rig,"worker",worker);
                else if(mode=="unstarted")
                {
                    unstarted=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>new SyntheticClient());Set(rig,"worker",unstarted);
                    var eyePumpType=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveStroopEyePump",true);
                    Set(rig,"eyePump",Activator.CreateInstance(eyePumpType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{unstarted},null));
                }
                if(mode=="open")
                {
                    var stimulus=staleStimulus;
                    Call(rig,"ApplyTrackedMask",true);
                    Require(camera.cullingMask==0&&!stimulus.gameObject.activeSelf,"tracked raw camera and stale stimulus must remain hidden without an open receipt");
                    stimulus.gameObject.SetActive(true);
                    camera.cullingMask=-1;Call(bootstrap,"Render");
                    Require(camera.cullingMask==0&&!stimulus.gameObject.activeSelf&&!trial.Onset.HasValue,
                        "raw Render enabled a stimulus before a verified trial-open receipt");
                    Require(((string)Get(bootstrap,"message")).Contains("Trial-open context observed in memory only"),
                        "actual trial-open request did not remain unreceipted and unknown");
                }
                else if(mode=="render")
                {
                    Require(!trial.Onset.HasValue,"synthetic trial unexpectedly had an onset");
                    // Only the dedicated raw callback carries the model's
                    // rendered text and colour. A generic callback cannot
                    // establish content matching or a proxy observation.
                    var observationType=typeof(WorldSpaceExperimentView).GetNestedType("StimulusRenderObservation",BindingFlags.NonPublic);
                    Require(observationType!=null,"raw stimulus observation type missing");
                    var colours=new Dictionary<Colour,Color>{{Colour.Red,Color.red},{Colour.Green,Color.green},{Colour.Blue,Color.blue},{Colour.Yellow,Color.yellow}};
                    Func<string,string,Color,object> observe=(id,text,ink)=>Activator.CreateInstance(observationType,
                        BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{id,1L,1,clock.Now,text,ink},null);
                    string word=trial.Stimulus.Word.ToString().ToUpperInvariant();Color inkColour=colours[trial.Stimulus.Ink];
                    const string diagnostic="Unity render-proxy callback and task content matched only";
                    Require(Get(bootstrap,"pendingRawPresentation")==null,"fixture must not hold a presentation reservation");
                    Set(bootstrap,"message","Synthetic raw callback fixture.");
                    Call(bootstrap,"OnViveRawStimulusRendered",observe("other-"+trial.TrialId,word,inkColour));
                    Call(bootstrap,"OnViveRawStimulusRendered",observe(trial.TrialId,"MISMATCH",inkColour));
                    Call(bootstrap,"OnViveRawStimulusRendered",observe(trial.TrialId,word,inkColour.Equals(Color.red)?Color.green:Color.red));
                    Require(!((string)Get(bootstrap,"message")).Contains(diagnostic)&&!trial.Onset.HasValue&&!(bool)Get(bootstrap,"rawSafetyLatched"),
                        "mismatched raw callback must not claim a proxy or mutate the trial");
                    Call(bootstrap,"OnViveRawStimulusRendered",observe(trial.TrialId,word,inkColour));
                    Require(((string)Get(bootstrap,"message")).Contains(diagnostic)&&!trial.Onset.HasValue&&!(bool)Get(bootstrap,"rawSafetyLatched"),
                        "matching raw callback must remain a non-mutating software proxy only");
                    Set(bootstrap,"message","Synthetic generic callback fixture.");
                    Call(bootstrap,"OnStimulusRendered",trial.TrialId,clock.Now);
                    Require(!trial.Onset.HasValue&&(bool)Get(bootstrap,"rawMutationIntervalUnknown")&&(bool)Get(bootstrap,"rawSafetyLatched")&&
                        camera.cullingMask==0&&!staleStimulus.gameObject.activeSelf&&
                        ((string)Get(bootstrap,"message")).Contains("last status interval is unknown")&&
                        !((string)Get(bootstrap,"message")).Contains(diagnostic),
                        "unguarded generic callback must hide the stimulus, fail closed and mark the interval unknown");
                }
                else if(mode=="participant-menu")
                {
                    Require(!(bool)Call(bootstrap,"HandleParticipantMenu"),"in-task menu must not be handled as pause");
                    Require(!(bool)Get(bootstrap,"rawSafetyLatched")&&task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending,
                        "participant menu unexpectedly stopped recording or interrupted the task");
                    Call(rig,"Update"); // The independent missing-eye fault must still mask safely.
                }
                else if(mode=="owner-pause")
                {
                    Call(bootstrap,"PauseTask");
                    Require((bool)Get(bootstrap,"rawSafetyLatched"),"explicit raw pause must latch recording safety");
                    var message=(string)Get(bootstrap,"message");
                    Require(message.Contains("unconfirmed tail is unknown")&&message.Contains("cannot resume"),
                        "explicit raw pause must disclose the uncertain, non-resumable recording tail");
                    Require(!(bool)pumpType.GetProperty("WithdrawalRequested",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pump,null),
                        "explicit safety pause must not claim participant withdrawal");
                }
                else if(mode=="stop-eye-busy")
                {
                    var gate=typeof(ViveProEyeWorker).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(worker);
                    Hold(gate,()=>{
                        var watch=Stopwatch.StartNew();Call(rig,"StopEye");
                        Require(watch.ElapsedMilliseconds<500&&camera.cullingMask==0&&(bool)Get(bootstrap,"rawSafetyLatched"),
                            "stop eye must revoke and mask without waiting for the busy worker");
                        Require(!(bool)Get(bootstrap,"rawEyeStopRequested"),"busy eye stop must remain pending");
                        Require(!(bool)pumpType.GetProperty("WithdrawalRequested",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pump,null),
                            "owner stop eye must not be labelled participant withdrawal");
                    });
                    Call(bootstrap,"ServiceNamedRawSafety");
                    Require((bool)Get(bootstrap,"rawEyeStopRequested")&&(bool)Get(rig,"stopping"),
                        "released worker must receive the pending nonblocking stop request");
                }
                else if(mode=="owner-pause-busy")
                {
                    var gate=typeof(ViveDevelopmentParticipant).GetField("namedRecordingGate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(task);
                    Hold(gate,()=>{
                        var watch=Stopwatch.StartNew();Call(bootstrap,"PauseTask");
                        Require(watch.ElapsedMilliseconds<500&&camera.cullingMask==0&&task.Phase==Phase.Practice,
                            "raw owner pause waited for task lock or claimed an unperformed pause");
                        Require(((string)Get(bootstrap,"message")).Contains("pending"),"busy safety pause must remain pending");
                    });
                    Call(bootstrap,"ServiceNamedRawSafety");
                }
                else Call(rig,"Update");
                if(mode=="technical-then-owner"||mode=="technical-then-menu")
                {
                    Require(task.Phase==Phase.Practice&&camera.cullingMask==0,"technical hold must precede explicit pause");
                    Call(bootstrap,mode=="technical-then-owner"?"PauseTask":"HandleParticipantMenu");
                }
                bool explicitPause=mode=="owner-pause"||mode=="owner-pause-busy"||mode=="technical-then-owner";
                Require(camera.cullingMask==0&&(explicitPause?task.Phase==Phase.Paused&&trial.Outcome==TrialOutcome.Interrupted:
                    task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending),mode+" safety state is incorrect");
                Require(explicitPause?task.PauseDiagnosticRevision==1:task.PauseDiagnosticRevision==0,
                    "a technical hold must not claim a confirmed pause diagnostic");
                var engine=(Experiment)Get(task,"experiment");JournalEvent pauseEvent=null;
                foreach(var ev in engine.Events)if(ev.Kind=="explicit_pause")pauseEvent=ev;
                Require(explicitPause?pauseEvent!=null&&pauseEvent.Fields["actor"]=="Owner":
                    pauseEvent==null,mode+" pause source was invented or incorrect");
                Require(!staleStimulus.gameObject.activeSelf,mode+" must hide a stale stimulus from any camera");
                Require(!(bool)pumpType.GetProperty("WithdrawalRequested",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pump,null),
                    mode+" stop was incorrectly labelled participant withdrawal");
            }
            finally
            {
                if(pump!=null)Call(pump,"Stop");if(client!=null)client.Release.Set();
                if(worker!=null){worker.Stop(1000);worker.Dispose();}if(unstarted!=null)unstarted.Dispose();
                if(rig!=null){Set(rig,"running",false);Set(rig,"worker",null);Set(rig,"stopping",false);}
                if(bootstrap!=null)Set(bootstrap,"namedRawPump",null);
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }
        static void VerifyWaitingBaseline(Scene scene,Camera camera,WorldSpaceExperimentView view)
        {
            GameObject rigObject=null,bootstrapObject=null;ViveStroopPreviewRig rig=null;
            ViveStroopDevelopmentBootstrap bootstrap=null;ViveProEyeWorker worker=null;SyntheticClient client=null;object pump=null;
            try
            {
                rigObject=Root("SyntheticBaselineRig",scene);rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=Root("SyntheticBaselineBootstrap",scene);bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-baseline-clock");client=new SyntheticClient();
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"synthetic baseline worker setup failed");
                var task=Task(clock);var admissionType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true);
                var admission=admissionType.GetMethod("CreateSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,32});
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"baseline fixture choices failed");
                // An otherwise formed synthetic admission and a sink object
                // must not open the closed installed-scene route or mutate the
                // task. The sink constructor below opens no path or file.
                var sinkType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveRawSessionJournalSink",true);
                var sink=Activator.CreateInstance(sinkType,BindingFlags.Instance|BindingFlags.NonPublic,null,
                    new object[]{admission,@"C:\synthetic-raw-fixture\never-opened.vraw"},null);
                Set(bootstrap,"participant",task);Set(bootstrap,"sharedClock",clock);Set(bootstrap,"configured",true);
                int beforeAttach=task.ReadEventsAfter(-1,4096).Count;
                var budgetType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveRawByteBudget",true);
                var budget=budgetType.GetMethod("SelectExplicit",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{33554432L});
                var ownerStart=typeof(ViveStroopDevelopmentBootstrap).GetMethod("CanStartWithOwnerOutput",BindingFlags.Static|BindingFlags.NonPublic);
                Require(ownerStart!=null,"owner start policy missing");
                foreach(bool ready in new[]{false,true})foreach(bool outputGate in new[]{false,true})foreach(bool ownerOutput in new[]{false,true})
                    Require((bool)ownerStart.Invoke(null,new object[]{ready,outputGate,ownerOutput})==(ready&&(!ownerOutput||outputGate)),
                        "recording-only and explicitly requested output must obey their respective closed gates");
                var candidatePolicy=typeof(ViveStroopDevelopmentBootstrap).GetMethod("CanRequestNamedRawLocalCandidate",BindingFlags.Static|BindingFlags.NonPublic);
                Require(candidatePolicy!=null,"owner local-candidate policy missing");
                foreach(bool gate in new[]{false,true})foreach(bool ownerReview in new[]{false,true})
                foreach(bool attempted in new[]{false,true})foreach(bool reviewWithdrawn in new[]{false,true})
                foreach(bool sourceClosed in new[]{false,true})foreach(bool destinationClosed in new[]{false,true})
                    Require((bool)candidatePolicy.Invoke(null,new object[]{gate,ownerReview,attempted,reviewWithdrawn,sourceClosed,destinationClosed})==
                        (gate&&ownerReview&&!attempted&&!reviewWithdrawn&&sourceClosed&&destinationClosed),
                        "a local candidate requires explicit one-time owner review, both closes, and no observed withdrawal");
                var contextPolicy=typeof(ViveStroopDevelopmentBootstrap).GetMethod("SameCandidateContext",BindingFlags.Static|BindingFlags.NonPublic);
                Require(contextPolicy!=null,"owner candidate context policy missing");
                var identities=new[]{new object(),new object(),new object(),new object(),new object()};
                var context=new object[]{identities[0],identities[0],identities[1],identities[1],identities[2],identities[2],
                    identities[3],identities[3],identities[4],identities[4],true,true,true};
                Require((bool)contextPolicy.Invoke(null,context),"same completed session should retain its claimed candidate");
                for(int i=1;i<10;i+=2)
                {
                    var changed=(object[])context.Clone();changed[i]=new object();
                    Require(!(bool)contextPolicy.Invoke(null,changed),"a changed session identity must hide the candidate");
                }
                for(int i=10;i<13;i++)
                {
                    var changed=(object[])context.Clone();changed[i]=false;
                    Require(!(bool)contextPolicy.Invoke(null,changed),"unfinished, active or unsafe state must hide the candidate");
                }
                Require(!(bool)Call(bootstrap,"TryRequestNamedRawLocalCandidate",false)&&
                    !(bool)Call(bootstrap,"TryRequestNamedRawLocalCandidate",true)&&
                    !(bool)Get(bootstrap,"namedRawLocalCandidateAttempted")&&Get(bootstrap,"namedRawLocalCandidate")==null,
                    "closed gates and missing destination must not claim or expose a local candidate");
                var csvState=typeof(ViveStroopDevelopmentBootstrap).GetMethod("CsvCandidateState",BindingFlags.Static|BindingFlags.NonPublic);
                Require(csvState!=null,"CSV state policy missing");
                Require(((string)csvState.Invoke(null,new object[]{false,false,false,false})).Contains("not requested"),"unrequested CSV must be distinct");
                Require(((string)csvState.Invoke(null,new object[]{true,false,false,false})).Contains("unconfirmed"),"raw close must not imply CSV success");
                Require(((string)csvState.Invoke(null,new object[]{true,false,false,true})).Contains("UNKNOWN")&&
                    ((string)csvState.Invoke(null,new object[]{true,true,false,false})).Contains("UNKNOWN")&&
                    ((string)csvState.Invoke(null,new object[]{true,true,true,true})).Contains("UNKNOWN"),"unknown and failed CSV review must stay unknown");
                Require(((string)csvState.Invoke(null,new object[]{true,true,true,false})).Contains("unverified"),"CSV receipt must remain unverified");
                var csvLimits=typeof(ViveStroopDevelopmentBootstrap).GetMethod("TryParseCsvLimits",BindingFlags.Static|BindingFlags.NonPublic);
                Require(csvLimits!=null,"CSV explicit limits parser missing");
                foreach(var limits in new[]{new[]{"","1"},new[]{"0","1"},new[]{"8193","1"},new[]{"1","0"},new[]{"1","3601"},new[]{"1.5","1"}})
                    Require(!(bool)csvLimits.Invoke(null,new object[]{limits[0],limits[1],0L,0}),"invalid CSV limits must be rejected");
                var validLimits=new object[]{"1","3600",0L,0};
                Require((bool)csvLimits.Invoke(null,validLimits)&&(long)validLimits[2]==1048576L&&(int)validLimits[3]==3600000,
                    "CSV limits must use independent explicit bytes and milliseconds");
                Require(!(bool)Call(bootstrap,"TryRequestNamedCsvLocalCandidate",false)&&!(bool)Call(bootstrap,"TryRequestNamedCsvLocalCandidate",true)&&
                    !(bool)Get(bootstrap,"namedCsvLocalCandidateAttempted")&&Get(bootstrap,"namedCsvLocalCandidate")==null,
                    "closed CSV gate must not claim or expose a path");
                foreach(var choices in new[]{new[]{false,false},new[]{true,false},new[]{false,true},new[]{true,true}})
                    Require(!(bool)Call(bootstrap,"TryCreateAndAttachNamedRawInstalled",@"C:\synthetic-raw-fixture\never-opened",budget,choices[0],choices[1])&&
                        Get(bootstrap,"namedRawPump")==null&&task.Phase==Phase.Preparation&&task.ReadEventsAfter(-1,4096).Count==beforeAttach&&worker.Started,
                        "closed creation gate must reject every software-selection combination without starting or mutating the task");
                foreach(var choices in new[]{new[]{false,false},new[]{true,false},new[]{false,true},new[]{true,true}})
                foreach(bool ownerOutput in new[]{false,true})
                    Require(!(bool)Call(bootstrap,"TryCreateAndAttachNamedRawInstalledWithOwnerOutput",@"C:\synthetic-raw-fixture\never-opened",budget,choices[0],choices[1],ownerOutput)&&
                        Get(bootstrap,"namedRawPump")==null&&Get(bootstrap,"namedRawDestination")==null&&task.Phase==Phase.Preparation&&
                        task.ReadEventsAfter(-1,4096).Count==beforeAttach&&worker.Started,
                        "closed recording and output gates must reject owner output requests without creating a destination or changing the task");
                Require(!(bool)Call(bootstrap,"TryAttachNamedRawInstalled",admission,sink)&&Get(bootstrap,"namedRawPump")==null&&
                    task.Phase==Phase.Preparation&&task.ReadEventsAfter(-1,4096).Count==beforeAttach&&worker.Started,
                    "closed installed attachment must not start a pump or mutate synthetic state");
                var pumpType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writerType=pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic);
                var writer=Activator.CreateInstance(writerType,BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{admission},null);
                pump=pumpType.GetMethod("StartSyntheticWithStatusForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,admission,writer});
                Set(bootstrap,"participant",task);Set(bootstrap,"configured",true);Set(bootstrap,"namedRawPump",pump);
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",bootstrap);Set(rig,"running",true);Set(rig,"worker",worker);camera.cullingMask=-1;
                var staleStimulus=(TextMesh)Get(view,"stimulusText");staleStimulus.gameObject.SetActive(true);
                var eyePumpType=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveStroopEyePump",true);
                Set(rig,"eyePump",Activator.CreateInstance(eyePumpType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{worker},null));
                int before=task.ReadEventsAfter(-1,4096).Count;
                Call(rig,"Update");
                Require(camera.cullingMask==0&&!staleStimulus.gameObject.activeSelf&&task.Phase==Phase.Preparation&&task.ReadEventsAfter(-1,4096).Count==before,
                    "initial baseline wait must block the camera without task input or Tick");
                Require(((string)Get(rig,"status")).Contains("Waiting for initial eye status"),"baseline wait status is missing");
                // Synthetic setup only: an active trial must not continue to
                // wait silently when no provider baseline has been observed.
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Call(task,"OwnerBeginPracticeCore",Actor.Owner);var trial=task.CurrentTrial;
                camera.cullingMask=-1;staleStimulus.gameObject.SetActive(true);
                Call(rig,"Update");
                // A technical loss has no owner/participant pause actor. The
                // safety policy stops recording and masks presentation while
                // leaving the unvalidated trial pending, rather than writing
                // a fabricated voluntary pause or interruption.
                bool hasExplicitPause=false;
                foreach(var item in task.ReadEventsAfter(-1,4096))if(item.Kind=="explicit_pause")hasExplicitPause=true;
                bool withdrawn=(bool)pumpType.GetProperty("WithdrawalRequested",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pump,null);
                Require(camera.cullingMask==0&&!staleStimulus.gameObject.activeSelf&&(bool)Get(bootstrap,"rawSafetyLatched")&&
                    bootstrap.RecordingBlocked&&task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending&&!hasExplicitPause&&!withdrawn,
                    "missing active baseline must block recording and presentation without inventing a voluntary pause; phase="+
                    task.Phase+", outcome="+trial.Outcome+", cameraMask="+camera.cullingMask+", explicitPause="+hasExplicitPause+", withdrawn="+withdrawn);
                Require(bootstrap.RecordingStatus.Contains("unconfirmed tail remain unknown"),
                    "owner recording status must mark the unconfirmed tail unknown after the safety stop");
                int afterSafety=task.ReadEventsAfter(-1,4096).Count;bool ordinaryRan=false;
                bool ordinaryAccepted=(bool)Call(bootstrap,"Mutate",new object[]{(Action)(()=>{ordinaryRan=true;task.Tick();})});
                Require(!ordinaryAccepted&&!ordinaryRan&&task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending&&
                    task.ReadEventsAfter(-1,4096).Count==afterSafety,
                    "ordinary task mutation must remain blocked after the baseline safety stop");
            }
            finally
            {
                if(pump!=null)Call(pump,"Stop");if(client!=null)client.Release.Set();if(worker!=null){worker.Stop(1000);worker.Dispose();}
                if(rig!=null){Set(rig,"running",false);Set(rig,"worker",null);Set(rig,"eyePump",null);Set(rig,"stopping",false);}
                if(bootstrap!=null)Set(bootstrap,"namedRawPump",null);
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }
        static void VerifyBlockPlanTimeout(Scene scene,Camera camera,WorldSpaceExperimentView view)
        {
            var deadline=typeof(ViveStroopDevelopmentBootstrap).GetMethod("IsWithinRawPlanDeadline",BindingFlags.Static|BindingFlags.NonPublic);
            Require(deadline!=null&&(bool)deadline.Invoke(null,new object[]{30d,29.999d})&&
                !(bool)deadline.Invoke(null,new object[]{30d,30d})&&!(bool)deadline.Invoke(null,new object[]{30d,30.001d})&&
                !(bool)deadline.Invoke(null,new object[]{30d,Double.NaN}),"plan deadline boundary must fail closed");
            var neutral=typeof(ViveStroopDevelopmentBootstrap).GetMethod("MustWaitForRawPlanNeutral",BindingFlags.Static|BindingFlags.NonPublic);
            var gate=new ViveStroopNeutralGate();gate.Observe(true,false,false,false,false,false);
            var held=gate.Observe(true,false,false,false,true,false);
            Require(!held.NeutralPending&&neutral!=null&&(bool)neutral.Invoke(null,new object[]{held.NeutralPending,true,false,false})&&
                (bool)neutral.Invoke(null,new object[]{false,false,true,false})&&(bool)neutral.Invoke(null,new object[]{false,false,false,true})&&
                !(bool)neutral.Invoke(null,new object[]{false,false,false,false}),
                "already-armed controller holds must delay receipt consumption until release");
            GameObject rigObject=null,bootstrapObject=null;ViveStroopPreviewRig rig=null;
            ViveStroopDevelopmentBootstrap bootstrap=null;ViveProEyeWorker worker=null;SyntheticClient client=null;object pump=null;
            try
            {
                rigObject=Root("SyntheticPlanTimeoutRig",scene);rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=Root("SyntheticPlanTimeoutBootstrap",scene);bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-plan-timeout-clock");client=new SyntheticClient();
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"synthetic timeout worker setup failed");
                var task=Task(clock);task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                var admissionType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true);
                var admission=admissionType.GetMethod("CreateSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,32});
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"timeout fixture choices failed");
                var pumpType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writerType=pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic);
                var writer=Activator.CreateInstance(writerType,BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{admission},null);
                pump=pumpType.GetMethod("StartSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,admission,writer});
                var prepared=Call(task,"PrepareNamedRecordingBlockPlan",Actor.Owner,true);
                var requestType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveBlockPlanRequest",true);
                object[] capture={task,clock,prepared,null};
                Require((bool)requestType.GetMethod("TryCapture",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,capture),"synthetic plan request unavailable");
                Set(bootstrap,"participant",task);Set(bootstrap,"sharedClock",clock);Set(bootstrap,"configured",true);Set(bootstrap,"namedRawPump",pump);
                Set(bootstrap,"rawPlanStartRequested",1);Set(bootstrap,"rawPendingBlockPlan",capture[3]);Set(bootstrap,"rawPlanDeadline",0d);
                Set(rig,"previewCamera",camera);camera.cullingMask=-1;
                Call(bootstrap,"ProcessNamedBlockPlanStart",true);
                Require((bool)Get(bootstrap,"rawSafetyLatched")&&camera.cullingMask==0&&task.Phase==Phase.Preparation&&task.CurrentTrial==null,
                    "expired plan wait must stop before practice or stimulus");
                Require(((string)Get(bootstrap,"message")).Contains("in-flight I/O is unknown"),"timeout must report unknown I/O");
                Require(!(bool)Call(capture[3],"TryValidateCurrentForReceipt",task,clock),"late request remained valid after timeout");
            }
            finally
            {
                if(pump!=null)Call(pump,"Stop");if(client!=null)client.Release.Set();if(worker!=null){worker.Stop(1000);worker.Dispose();}
                if(bootstrap!=null)Set(bootstrap,"namedRawPump",null);
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }
        static void VerifyOwnerCommandIsQueued(Scene scene,Camera camera,WorldSpaceExperimentView view)
        {
            GameObject rigObject=null,bootstrapObject=null;ViveStroopPreviewRig rig=null;
            ViveStroopDevelopmentBootstrap bootstrap=null;ViveProEyeWorker worker=null;SyntheticClient client=null;object pump=null;
            try
            {
                rigObject=Root("SyntheticQueuedOwnerRig",scene);rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=Root("SyntheticQueuedOwnerBootstrap",scene);bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-queued-owner-clock");client=new SyntheticClient();
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"synthetic queued-owner worker setup failed");
                var task=Task(clock);var admissionType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true);
                var admission=admissionType.GetMethod("CreateSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,32});
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"queued-owner fixture choices failed");
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Call(task,"OwnerBeginPracticeCore",Actor.Owner);
                // Fixture-only direct engine actions create a block-preparation
                // state. They do not supply a real block-plan receipt or data.
                var engine=(Experiment)Get(task,"experiment");
                while(engine.Phase==Phase.Practice)
                {
                    var trial=engine.CurrentTrial;engine.MarkPresented(trial.TrialId);
                    Require(engine.ButtonChanged(trial.Stimulus.Ink,true),"synthetic practice response failed");
                    engine.ButtonChanged(trial.Stimulus.Ink,false);
                }
                Require(task.Phase==Phase.BlockPreparation&&task.InputReady&&task.NamedPlan!=null,"queued-owner setup phase unavailable");
                var pumpType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writerType=pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic);
                var writer=Activator.CreateInstance(writerType,BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{admission},null);
                pump=pumpType.GetMethod("StartSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{task,worker,clock,admission,writer});
                Set(bootstrap,"participant",task);Set(bootstrap,"sharedClock",clock);Set(bootstrap,"configured",true);Set(bootstrap,"namedRawPump",pump);
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",bootstrap);camera.cullingMask=-1;
                int calls=0;Action owner=()=>{calls++;task.OwnerConfirmNamedPlan(Actor.Owner,"synthetic-owner");};
                Call(bootstrap,"Run",owner);
                Require(calls==0&&task.Phase==Phase.BlockPreparation&&Get(bootstrap,"pendingRawOwnerAction")!=null,
                    "owner action must wait for a fresh input/eye frame rather than run on OnGUI");
                Call(bootstrap,"Run",owner);
                Require(calls==0&&Get(bootstrap,"pendingRawOwnerAction")!=null,"a duplicate owner click must not run or replace the queued action");
                Require((bool)Call(bootstrap,"PendingRawOwnerStillCurrent"),"newly queued task stamp must be current");
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Require(task.Phase==Phase.BlockPreparation&&!(bool)Call(bootstrap,"PendingRawOwnerStillCurrent"),
                    "same-phase task mutation must invalidate a queued owner action");
                Call(bootstrap,"PauseTask");
                Require(calls==0&&Get(bootstrap,"pendingRawOwnerAction")==null&&camera.cullingMask==0&&
                    (bool)Get(bootstrap,"rawSafetyLatched"),"safety must clear a queued owner action without executing it");
            }
            finally
            {
                if(pump!=null)Call(pump,"Stop");if(client!=null)client.Release.Set();if(worker!=null){worker.Stop(1000);worker.Dispose();}
                if(bootstrap!=null)Set(bootstrap,"namedRawPump",null);
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }
        static void VerifyInstallFailureLifetime(Scene scene,Camera camera,WorldSpaceExperimentView view)
        {
            GameObject rigObject=null,bootstrapObject=null;ViveStroopPreviewRig rig=null;ViveProEyeWorker worker=null;SyntheticClient client=null;
            try
            {
                rigObject=Root("SyntheticInstallFailureRig",scene);rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=Root("SyntheticInstallFailureBootstrap",scene);var bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();
                bootstrap.Configure(rig,view);var clock=new MonotonicClock("synthetic-install-failure");client=new SyntheticClient();
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"install-failure worker setup");
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",bootstrap);Set(rig,"worker",worker);
                Call(rig,"LatchRawInstallFailure");UnityEngine.Object.DestroyImmediate(bootstrapObject);bootstrapObject=null;
                var gate=typeof(ViveProEyeWorker).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(worker);
                Hold(gate,()=>{var watch=Stopwatch.StartNew();Call(rig,"OnDisable");
                    Require(watch.ElapsedMilliseconds<500&&camera.cullingMask==0,"destroyed bootstrap must not cause a blocking stop or visible task");});
                Call(rig,"ServiceRawInstallFailure");Require((bool)Get(rig,"stopping"),"install-failure stop must proceed when worker lock is released");
            }
            finally
            {
                if(client!=null)client.Release.Set();if(worker!=null){worker.Stop(1000);worker.Dispose();}
                if(rig!=null){Set(rig,"worker",null);Set(rig,"stopping",false);}
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }
        // This invokes renderer callbacks synthetically; it does not prove
        // that a headset displayed the notice or that a wearer read it.
        static void VerifyRecordingSetup(Scene scene,Camera camera,WorldSpaceExperimentView view)
        {
            GameObject rigObject=null,bootstrapObject=null;ViveStroopDevelopmentBootstrap bootstrap=null;
            try
            {
                rigObject=Root("SyntheticRecordingChoiceRig",scene);var rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=Root("SyntheticRecordingChoiceBootstrap",scene);bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();
                bootstrap.Configure(rig,view);
                var setupType=typeof(ViveNamedRecordingUiAdmission).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRecordingSetup",true);
                var prepare=setupType.GetMethod("TryPrepare",BindingFlags.Static|BindingFlags.NonPublic);
                Require(prepare!=null,"synthetic recording choice factory unavailable");
                object[] prepareArgs={true,@"C:\Synthetic","128",(Func<string,bool>)(_=>true),null};
                var setup=prepare.Invoke(null,prepareArgs);
                Require(setup!=null,"synthetic recording choice setup");
                var match=typeof(WorldSpaceExperimentView).GetMethod("RecordingChoiceTextMatches",BindingFlags.Static|BindingFlags.NonPublic);
                Require(match!=null&&(bool)match.Invoke(null,new object[]{"Allow task\nrecording", "Allow task recording"})&&
                    !(bool)match.Invoke(null,new object[]{"Allow task ... (see owner/log)", "Allow task recording"}),
                    "wrapped text must match but truncated text must not establish a choice proxy");
                Set(bootstrap,"recordingSetup",setup);Set(bootstrap,"hand",VivePreviewControllerHand.Left);bootstrap.PrepareInputBeforeXr();
                Require(Get(bootstrap,"device")!=null,"recording choice must register controller input before XR");
                string task="Synthetic task recording choice";
                Call(view,"SetViveParticipantRecordingChoice",task,11);
                Call(view,"BeforeCameraCull",camera);
                Require(!(bool)Call(view,"WasViveRecordingChoiceRendered",11,Time.frameCount+1),"unrendered notice cannot be accepted");
                Call(view,"ObserveViveRecordingChoiceElement",camera,1);
                Call(view,"AfterCameraRender",camera);
                Require(!(bool)Call(view,"WasViveRecordingChoiceRendered",11,Time.frameCount+1),"one rendered notice element is insufficient");
                Call(view,"BeforeCameraCull",camera);Call(view,"ObserveViveRecordingChoiceElement",camera,1);
                Call(view,"ObserveViveRecordingChoiceElement",camera,2);
                ((TextMesh)Get(view,"previewInputText")).text="Synthetic wrong controls";Call(view,"AfterCameraRender",camera);
                Require(!(bool)Call(view,"WasViveRecordingChoiceRendered",11,Time.frameCount+1),"changed controls cannot establish a choice proxy");
                Call(view,"SetViveParticipantRecordingChoice",task,11);
                Call(view,"BeforeCameraCull",camera);Call(view,"ObserveViveRecordingChoiceElement",camera,1);
                Call(view,"ObserveViveRecordingChoiceElement",camera,2);Call(view,"AfterCameraRender",camera);
                Require((bool)Call(view,"WasViveRecordingChoiceRendered",11,Time.frameCount+1),"matching synthetic render proxy missing");
                Call(view,"SetViveParticipantRecordingChoice","Synthetic eye recording choice",12);
                Require(!(bool)Call(view,"WasViveRecordingChoiceRendered",12,Time.frameCount+1),"a new notice must not reuse a prior proxy");
                bootstrap.AfterXrStop();
                var current=setupType.GetProperty("Current",BindingFlags.Instance|BindingFlags.NonPublic);
                Require(current!=null&&Get(bootstrap,"device")==null&&String.Equals(current.GetValue(setup,null).ToString(),"Cancelled",StringComparison.Ordinal),
                    "XR stop must dispose input and cancel choices");
            }
            finally
            {
                if(bootstrap!=null)bootstrap.AfterXrStop();
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene scene=default(Scene);bool opened=false;GameObject cameraObject=null,viewObject=null,rigObject=null,bootstrapObject=null;
            ViveStroopPreviewRig rig=null;ViveStroopDevelopmentBootstrap bootstrap=null;ViveProEyeWorker worker=null;
            SyntheticClient client=null;object writer=null,pump=null;
            try
            {
                scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);opened=scene.IsValid()&&scene.isLoaded;Require(opened,"cannot open isolated scene");
                cameraObject=Root("SyntheticRawCamera",scene);var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=-1;
                viewObject=Root("SyntheticRawView",scene);var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                VerifyRecordingSetup(scene,camera,view);
                view.ConfigureViveDevelopmentPreview(new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}},.5);
                VerifyUnavailableBeforeTask(scene,camera,view,"missing");
                VerifyUnavailableBeforeTask(scene,camera,view,"stopping");
                VerifyUnavailableBeforeTask(scene,camera,view,"unstarted");
                VerifyUnavailableBeforeTask(scene,camera,view,"open");
                VerifyUnavailableBeforeTask(scene,camera,view,"render");
                VerifyUnavailableBeforeTask(scene,camera,view,"participant-menu");
                VerifyUnavailableBeforeTask(scene,camera,view,"owner-pause");
                VerifyUnavailableBeforeTask(scene,camera,view,"owner-pause-busy");
                VerifyUnavailableBeforeTask(scene,camera,view,"stop-eye-busy");
                VerifyUnavailableBeforeTask(scene,camera,view,"stale-menu");
                VerifyUnavailableBeforeTask(scene,camera,view,"technical-then-owner");
                VerifyUnavailableBeforeTask(scene,camera,view,"technical-then-menu");
                VerifyWaitingBaseline(scene,camera,view);
                VerifyBlockPlanTimeout(scene,camera,view);
                VerifyOwnerCommandIsQueued(scene,camera,view);
                VerifyInstallFailureLifetime(scene,camera,view);
                camera.cullingMask=-1;
                rigObject=Root("SyntheticRawRig",scene);rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=Root("SyntheticRawBootstrap",scene);bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-raw-fixture-clock");client=new SyntheticClient();
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"synthetic worker failed to start");
                var eyePumpType=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveStroopEyePump",true);
                var cachedPump=Activator.CreateInstance(eyePumpType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{worker},null);
                var workerGate=typeof(ViveProEyeWorker).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(worker);
                Hold(workerGate,()=>{
                    var watch=Stopwatch.StartNew();bool began=(bool)Call(cachedPump,"TryBeginRecordingFrame",worker);
                    Require(!began&&watch.ElapsedMilliseconds<500,"busy worker snapshot blocked or was accepted");
                    Require(!(bool)Get(cachedPump,"active")&&Get(cachedPump,"frame")==null,"failed worker snapshot left an active or stale frame");
                });
                var task=Task(clock);var trial=task.CurrentTrial;
                var admissionType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true);
                var create=admissionType.GetMethod("CreateSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic);
                Require(create!=null,"synthetic admission unavailable");var admission=create.Invoke(null,new object[]{task,worker,clock,32});
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"synthetic choices failed");
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Call(task,"OwnerBeginPracticeCore",Actor.Owner);trial=task.CurrentTrial;
                object[] stage={false};Require((bool)Call(admission,"TryStageNextTaskEvent",stage)&&(bool)stage[0],"synthetic task event was not staged");
                var pumpType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writerType=pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic);
                writer=Activator.CreateInstance(writerType,BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{admission},null);Set(writer,"Block",true);
                var start=pumpType.GetMethod("StartSyntheticForTest",BindingFlags.Static|BindingFlags.NonPublic);Require(start!=null,"synthetic pump unavailable");
                pump=start.Invoke(null,new object[]{task,worker,clock,admission,writer});
                var entered=(ManualResetEvent)Get(writer,"Entered");var release=(ManualResetEvent)Get(writer,"Release");
                Require(entered.WaitOne(3000),"synthetic append did not enter");
                Set(bootstrap,"participant",task);Set(bootstrap,"configured",true);Set(bootstrap,"namedRawPump",pump);
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",bootstrap);Set(rig,"worker",worker);Set(rig,"running",true);
                var blockedStimulus=(TextMesh)Get(view,"stimulusText");blockedStimulus.gameObject.SetActive(true);
                Call(pump,"Stop");
                var taskGate=typeof(ViveDevelopmentParticipant).GetField("namedRecordingGate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(task);
                Hold(taskGate,()=>{
                    var watch=Stopwatch.StartNew();Call(rig,"Update");
                    Require(watch.ElapsedMilliseconds<500,"blocked frame waited for the task lock");
                    Require(camera.cullingMask==0&&!blockedStimulus.gameObject.activeSelf&&task.Phase==Phase.Practice,"contested frame did not block presentation or mutated the task");
                    Require(((string)Get(bootstrap,"message")).Contains("pending"),"lock contention was not reported pending");
                });
                Call(rig,"Update");Require(camera.cullingMask==0&&task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending,"technical fault must remain masked without inventing an actor");
                var viewGate=typeof(ViveDevelopmentParticipant).GetField("namedViewGate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(task);
                Hold(viewGate,()=>{
                    // A later explicit external withdrawal must upgrade the
                    // previously applied stop/pause, even while I/O is pending.
                    Call(pump,"Withdraw");var watch=Stopwatch.StartNew();Call(rig,"Update");
                    Require(watch.ElapsedMilliseconds<500&&task.Phase==Phase.Practice,"withdraw waited for the view lock or claimed completion");
                    Require(((string)Get(bootstrap,"message")).Contains("pending"),"withdrawal was not reported pending");
                });
                Call(rig,"Update");Require(camera.cullingMask==0&&task.Phase==Phase.Ended&&task.EndReason==EndReason.Withdrawn,"withdrawal was not applied after lock release");
                Call(bootstrap,"WithdrawTask");Require(task.Phase==Phase.Ended&&task.EndReason==EndReason.Withdrawn,"repeated withdraw changed the ended task");
                var state=pumpType.GetProperty("State",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pump,null).ToString();
                Require(state=="IoUncertain", "reserved synthetic append must remain uncertain");
                Require(((string)Get(bootstrap,"message")).Contains("may still be in progress"),"unconfirmed cleanup must remain visible after task withdrawal");
                release.Set();client.Release.Set();
                Require(SpinWait.SpinUntil(()=>((bool)pumpType.GetProperty("HasExited",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pump,null)),3000),"synthetic writer did not exit");
                UnityEngine.Debug.Log("VIVE_RAW_PUMP_SAFETY_SYNTHETIC_FIXTURE_PASSED: blocked rig, pending locks, pause, withdrawal and in-flight uncertainty; no XR, vendor eye data or files.");
            }
            finally
            {
                if(writer!=null){var release=Get(writer,"Release") as ManualResetEvent;if(release!=null)release.Set();}
                if(client!=null)client.Release.Set();if(worker!=null){worker.Stop(1000);worker.Dispose();}
                if(rig!=null){Set(rig,"running",false);Set(rig,"worker",null);}
                if(bootstrap!=null)Set(bootstrap,"namedRawPump",null);
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(opened&&scene.IsValid()&&scene.isLoaded)Require(EditorSceneManager.CloseScene(scene,true),"cannot close fixture scene");
            }
        }
    }
}
#endif
