using System;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Pure in-memory synthetic model. It has no Experiment, renderer, worker,
    // storage, consent or production attachment and cannot classify a real
    // trial. A complete synthetic sequence is still UNKNOWN for research and
    // durability. Fault/withdrawal makes even a completed model unknown.
    internal sealed class ViveTrialTransactionCandidate
    {
        const int New=0,Open=1,Render=2,Tentative=3,Final=4,Fault=8,Withdrawn=16;
        readonly Thread owner;readonly string session,trial;readonly object run=new object();readonly long revision;
        int state;double openLocal,renderLocal,renderUnity,actionLocal;long layout;int frame,syntheticAction;
        internal ViveTrialTransactionCandidate(string syntheticSession,string syntheticTrial,long syntheticRevision)
        {
            if(!SyntheticId(syntheticSession)||!SyntheticId(syntheticTrial)||syntheticRevision<0)
                throw new ArgumentException("Synthetic identities and a nonnegative revision are required.");
            session=syntheticSession;trial=syntheticTrial;revision=syntheticRevision;owner=Thread.CurrentThread;
        }
        static bool SyntheticId(string value)
        {return value!=null&&value.StartsWith("synthetic-",StringComparison.Ordinal)&&value.Length>10&&value.Length<=128;}
        static bool TimeValue(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=0;}
        internal object SyntheticRun {get{return run;}}
        internal string ActualValidity {get{return "UNKNOWN";}}
        internal bool DurabilityProven {get{return false;}}
        internal bool ResearchAdmission {get{return false;}}
        internal bool SyntheticSequenceCompleted {get{return Volatile.Read(ref state)==Final;}}
        internal bool TerminalUnknown {get{return (Volatile.Read(ref state)&(Fault|Withdrawn))!=0;}}
        bool OwnerAndIdentity(string candidateSession,string candidateTrial,object candidateRun,long candidateRevision)
        {return Object.ReferenceEquals(Thread.CurrentThread,owner)&&String.Equals(session,candidateSession,StringComparison.Ordinal)&&
            String.Equals(trial,candidateTrial,StringComparison.Ordinal)&&Object.ReferenceEquals(run,candidateRun)&&revision==candidateRevision;}
        bool Advance(int from,int to)
        {
            if(Interlocked.CompareExchange(ref state,to,from)==from)return true;
            Fail();return false;
        }
        void Terminal(int bit)
        {while(true){int old=Volatile.Read(ref state);if((old&bit)!=0)return;if(Interlocked.CompareExchange(ref state,old|bit,old)==old)return;}}
        internal void Fail(){Terminal(Fault);}
        internal void Withdraw(){Terminal(Withdrawn);}
        internal bool OpenSynthetic(string candidateSession,string candidateTrial,object candidateRun,long candidateRevision,double localTime)
        {
            if(!OwnerAndIdentity(candidateSession,candidateTrial,candidateRun,candidateRevision)||!TimeValue(localTime)){Fail();return false;}
            openLocal=localTime;return Advance(New,Open);
        }
        internal bool ObserveSyntheticRender(string candidateSession,string candidateTrial,object candidateRun,long candidateRevision,
            long presentationRevision,int frameNumber,double unityRenderProxy,double localObservation)
        {
            if(!OwnerAndIdentity(candidateSession,candidateTrial,candidateRun,candidateRevision)||presentationRevision<0||frameNumber<0||
                !TimeValue(unityRenderProxy)||!TimeValue(localObservation)||localObservation<openLocal){Fail();return false;}
            layout=presentationRevision;frame=frameNumber;renderUnity=unityRenderProxy;renderLocal=localObservation;return Advance(Open,Render);
        }
        internal bool TentativeSyntheticAction(string candidateSession,string candidateTrial,object candidateRun,long candidateRevision,int actionCode,double localTime)
        {
            if(!OwnerAndIdentity(candidateSession,candidateTrial,candidateRun,candidateRevision)||actionCode<0||actionCode>3||
                !TimeValue(localTime)||localTime<renderLocal){Fail();return false;}
            syntheticAction=actionCode;actionLocal=localTime;return Advance(Render,Tentative);
        }
        // This is a synthetic state transition, not an I/O receipt or a proof
        // that any real task, render, eye status or response was observed.
        internal bool FinalizeSynthetic(string candidateSession,string candidateTrial,object candidateRun,long candidateRevision,double localTime)
        {
            if(!OwnerAndIdentity(candidateSession,candidateTrial,candidateRun,candidateRevision)||!TimeValue(localTime)||localTime<actionLocal||
                layout<0||frame<0||!TimeValue(renderUnity)||syntheticAction<0){Fail();return false;}
            return Advance(Tentative,Final);
        }
    }
}
