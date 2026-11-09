using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using Microsoft.Win32.SafeHandles;

namespace AvatarStroop.Tests
{
    public static class ViveRawDirectoryLeaseTests
    {
        sealed class SyntheticClient:IViveProEyeClient
        {
            internal readonly ManualResetEvent Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Release.WaitOne(3000);return null;}
            public void Dispose(){Release.Set();}
        }
        static ViveDevelopmentParticipant SyntheticTask(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"directory-fixture-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"directory-fixture-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"directory-fixture-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("directory-fixture-session",37,plan,intent);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"directory-fixture-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        // Invalid non-owning SafeHandles mean tests never touch the filesystem
        // or close a real OS handle; reflection reaches only the private
        // in-memory lease constructor, not a native-open or file-create path.
        static ViveRawDirectoryLease FakeLease(out ViveRawDirectoryLease.LeaseHandle fake)
        {
            fake=new ViveRawDirectoryLease.LeaseHandle(IntPtr.Zero,false);
            var ctor=typeof(ViveRawDirectoryLease).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,
                new[]{typeof(List<ViveRawDirectoryLease.LeaseHandle>)},null);
            return (ViveRawDirectoryLease)ctor.Invoke(new object[]{new List<ViveRawDirectoryLease.LeaseHandle>{fake}});
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE raw directory syntax rejects ambiguous and remote candidates",()=>{
                foreach(string path in new[]{null,"",@"C:\",@"C:folder",@"\\server\share",@"\\?\C:\folder",
                    @"C:\folder\..\other",@"C:\folder\.\other",@"C:\folder\\other",@"C:\folder.\other",
                    @"C:\folder \other",@"C:\folder:stream",@"C:\CON.txt",@"C:\LPT1\other","C:\\COM\u00b9.txt",
                    "C:\\LPT\u00b2\\other","C:\\folder\\bad\u0001name",@"C:/folder"})
                    Check.Throws<ArgumentException>(()=>ViveRawDirectoryLease.NormalizeCandidate(path));
            });
            suite.Run("VIVE raw directory syntax accepts a canonical candidate",()=>{
                string selected=ViveRawDirectoryLease.NormalizeCandidate(@"C:\synthetic\test");
                Check.Equal(@"C:\synthetic\test",selected);
                // Parsing is not authorization and does not touch the filesystem.
            });
            suite.Run("VIVE directory lease transfers once and original holder cannot close it",()=>{
                ViveRawDirectoryLease.LeaseHandle fake;var lease=FakeLease(out fake);ViveRawDirectoryLease.ExclusiveOwner owner,second;
                Check.True(lease.TryTakeExclusive(out owner));Check.True(owner!=null);
                Check.True(!lease.TryTakeExclusive(out second));Check.Equal((ViveRawDirectoryLease.ExclusiveOwner)null,second);
                lease.Dispose();Check.True(lease.ExternalDisposeRequested);Check.True(!fake.IsClosed);
                bool complete;Check.True(!lease.TryCloseNextExclusive(owner,null,out complete));Check.True(!complete&&!fake.IsClosed);
                ViveRawDirectoryLease.DirectoryIdentity ignored;
                Check.True(!lease.TryReadExclusiveDirectoryIdentity(new ViveRawDirectoryLease.ExclusiveOwner(lease),out ignored));
                // A different same-process token is rejected before any OS call.
                using(var candidate=new SafeFileHandle(new IntPtr(123),false))
                {
                    Check.True(!lease.HasSameOpenDirectoryIdentity(candidate));
                    Check.True(!lease.HasSameOpenDirectoryIdentity(candidate,new ViveRawDirectoryLease.ExclusiveOwner(lease)));
                }
            });
            suite.Run("VIVE directory identity snapshots compare complete volume and 128-bit values",()=>{
                var source=new byte[16];source[0]=7;var id=new ViveRawDirectoryLease.DirectoryIdentity(11,source);
                source[0]=9;var same=new byte[16];same[0]=7;
                Check.True(id.Matches(11,same));Check.True(!id.Matches(12,same));same[15]=1;
                Check.True(!id.Matches(11,same));Check.True(!id.Matches(11,null));
                Check.Equal(120,ViveRawDirectoryLease.MaxAncestorHandles);
            });
            suite.Run("VIVE native directory return slot records raw handle before wrapper",()=>{
                var observed=new ViveRawDirectoryLease.LeaseOpenObservation();Check.True(!observed.ApiReturned);
                observed.RecordReturned(new IntPtr(12345),0);Check.True(observed.ApiReturned);
                Check.Equal(new IntPtr(12345),observed.Raw);Check.Equal((ViveRawDirectoryLease.LeaseHandle)null,observed.Managed);
                Check.Equal((int?)null,observed.NativeError);
                Check.Throws<InvalidOperationException>(()=>observed.RecordReturned(new IntPtr(6789),0));
                var wrapper=new ViveRawDirectoryLease.LeaseHandle(IntPtr.Zero,false);observed.BindManaged(wrapper);
                Check.True(Object.ReferenceEquals(wrapper,observed.Managed));
                Check.Throws<InvalidOperationException>(()=>observed.BindManaged(wrapper));
                var denied=new ViveRawDirectoryLease.LeaseOpenObservation();denied.RecordReturned(new IntPtr(-1),5);
                Check.True(denied.ApiReturned);Check.Equal((int?)5,denied.NativeError);Check.Equal((ViveRawDirectoryLease.LeaseHandle)null,denied.Managed);
                Check.Throws<InvalidOperationException>(()=>denied.RecordReturned(new IntPtr(-1),5));
                var contradictory=new ViveRawDirectoryLease.LeaseOpenObservation();
                Check.Throws<InvalidOperationException>(()=>contradictory.RecordReturned(new IntPtr(123),5));
                var native=typeof(ViveRawDirectoryLease).GetMethod("CreateFile3Raw",BindingFlags.Static|BindingFlags.NonPublic);
                Check.True(native!=null);Check.Equal(typeof(IntPtr),native.ReturnType);
            });
            suite.Run("VIVE failed lease open reservation remains uncertain and cannot retry",()=>{
                var identity=(ViveNamedSessionFrameAdmission)FormatterServices.GetUninitializedObject(typeof(ViveNamedSessionFrameAdmission));
                var fence=new ViveRawIoFence(identity,new ViveRawRevocation());Exception error=null;
                var worker=new Thread(()=>{try{
                    ViveRawIoFence.Permit permit;Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.LeaseOpen,out permit));
                    // A synthetic API failure, including an observed access
                    // denial, is not a reason to repeat the native call.
                    Check.Equal(ViveRawIoFence.IoOutcome.Unknown,fence.Finish(permit,true,false));
                    Check.True(!fence.TryReserve(ViveRawIoFence.IoOperation.LeaseOpen,out permit));
                    Check.True(!fence.TryReserve(ViveRawIoFence.IoOperation.CloseAncestor,out permit));
                }catch(Exception e){error=e;}});
                Check.True(fence.BindWriter(worker));worker.Start();Check.True(worker.Join(3000));if(error!=null)throw error;
                Check.True(fence.IsFaulted&&fence.IsRevoked&&fence.OutcomeUncertain);
            });
            suite.Run("VIVE directory lease transfer and dispose have one race winner",()=>{
                for(int i=0;i<250;i++)
                {
                    ViveRawDirectoryLease.LeaseHandle fake;var lease=FakeLease(out fake);var start=new ManualResetEvent(false);
                    bool transferred=false;ViveRawDirectoryLease.ExclusiveOwner acquired=null;
                    var worker=new Thread(()=>{start.WaitOne();transferred=lease.TryTakeExclusive(out acquired);});
                    worker.Start();start.Set();lease.Dispose();Check.True(worker.Join(3000));start.Dispose();
                    ViveRawDirectoryLease.ExclusiveOwner later;Check.True(!lease.TryTakeExclusive(out later));
                    if(transferred){Check.True(acquired!=null);Check.True(!fake.IsClosed);Check.True(lease.ExternalDisposeRequested);}
                    else Check.True(acquired==null);
                }
            });
            suite.Run("VIVE directory lease dispose does not wait for a busy identity lock",()=>{
                ViveRawDirectoryLease.LeaseHandle fake;var lease=FakeLease(out fake);
                var gate=typeof(ViveRawDirectoryLease).GetField("lifecycle",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(lease);
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var worker=new Thread(()=>{lock(gate){entered.Set();release.WaitOne(3000);}});
                try
                {
                    worker.Start();Check.True(entered.WaitOne(3000));lease.Dispose();
                    Check.True(lease.DisposalPending);Check.True(!fake.IsClosed);
                    ViveRawDirectoryLease.ExclusiveOwner owner;Check.True(!lease.TryTakeExclusive(out owner));
                }
                finally{release.Set();Check.True(worker.Join(3000));entered.Dispose();release.Dispose();}
                Check.True(lease.DisposalPending);Check.True(!fake.IsClosed);
            });
            suite.Run("VIVE directory configuration never opens a path and rejects legacy preopened lease",()=>{
                var clock=new MonotonicClock("directory-fixture-clock");var client=new SyntheticClient();
                var worker=new ViveProEyeWorker(clock,()=>null,()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(SpinWait.SpinUntil(()=>worker.Started,3000));
                    var task=SyntheticTask(clock);var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,8);
                    var budget=ViveRawByteBudget.SelectExplicit(4096);
                    // The owner path may contain a trailing separator. The
                    // sink compares exact normalized values, so output arming
                    // must use the same canonical string as the source sink.
                    string requested=@"C:\synthetic-does-not-exist\";
                    string normalized=ViveRawDirectoryLease.NormalizeCandidate(requested);
                    var selected=new ViveRawSessionJournalSink(admission,requested,budget,true);
                    Check.True(selected.HasNativeConfiguration&&!selected.ProductionGateOpen);
                    Check.Equal(@"C:\synthetic-does-not-exist",normalized);
                    Check.True(selected.HasExactSelectedDirectory(admission,admission.IoFence,normalized));
                    Check.True(!selected.HasExactSelectedDirectory(admission,admission.IoFence,requested));
                    Check.Throws<ArgumentException>(()=>new ViveRawSessionJournalSink(admission,@"C:\synthetic",budget,false));
                    ViveRawDirectoryLease.LeaseHandle fake;var oldLease=FakeLease(out fake);
                    var legacy=new ViveRawSessionJournalSink(admission,oldLease,@"C:\synthetic",budget);
                    Check.True(!legacy.HasNativeConfiguration&&!legacy.ProductionGateOpen);
                    int before=ViveRawDirectoryLease.FencedRetainedCount;ViveRawDirectoryLease opened;
                    Check.True(!ViveRawDirectoryLease.TryOpenExistingFenced(admission,admission.IoFence,@"C:\synthetic-does-not-exist",null,out opened));
                    Check.Equal((ViveRawDirectoryLease)null,opened);Check.Equal(before,ViveRawDirectoryLease.FencedRetainedCount);
                    oldLease.Dispose();
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
        }
    }
}
