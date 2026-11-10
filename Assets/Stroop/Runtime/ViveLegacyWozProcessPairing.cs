using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Versioned manual laboratory capability, not encryption or physical identity.
    // Unlike the diagnostic token, this preserves both EXISTING role clock IDs
    // and the actual participant session. Import never constructs a clock/choice.
    public sealed class ViveLegacyWozProcessPairing : IDisposable
    {
        const string Prefix="WOZPROC1.";
        const string RecordingPrefix="WOZPROC2.";
        static readonly object claimGate=new object();
        static readonly HashSet<string> spentOperatorEpochs=new HashSet<string>(StringComparer.Ordinal);
        readonly byte[] key,epoch;
        readonly ViveLegacyWozTaskOwner.SourceChoice localChoice;
        bool disposed,participantClaimed,operatorClaimed;
        public WozLanContext Context {get;private set;}
        public string SourceOwner {get;private set;}
        public long SourceGeneration {get;private set;}
        public long ChoiceGeneration {get;private set;}
        public bool ParticipantLocalRecordingDeclared {get;private set;}
        internal string EpochLabel {get{return BitConverter.ToString(epoch).Replace("-","");}}
        ViveLegacyWozProcessPairing(byte[] key,byte[] epoch,string session,string participantClock,string operatorClock,
            string sourceOwner,long sourceGeneration,long choiceGeneration,ControlSource mode,ViveLegacyWozTaskOwner.SourceChoice choice,bool recording=false)
        {
            if(key==null||key.Length!=32||epoch==null||epoch.Length!=16||sourceGeneration<0||choiceGeneration<1||
                (mode!=ControlSource.LiveOperator&&mode!=ControlSource.Simulator))throw new ArgumentException("Invalid process pairing");
            WireCodec.RequireIdentity(sourceOwner);
            this.key=(byte[])key.Clone();this.epoch=(byte[])epoch.Clone();localChoice=choice;
            SourceOwner=sourceOwner;SourceGeneration=sourceGeneration;ChoiceGeneration=choiceGeneration;
            ParticipantLocalRecordingDeclared=recording;
            string stream=mode==ControlSource.Simulator?"simulator/legacy-process/"+EpochLabel:
                ViveDevelopmentTransport.OperatorStream(ViveProEyeRehearsalCoordinates.MappingVersion,EpochLabel);
            Context=new WozLanContext(new PeerIdentity(session,"legacy-participant",PeerRole.Participant,"legacy-task",mode,participantClock),
                new PeerIdentity(session,"legacy-operator",PeerRole.Operator,stream,mode,operatorClock),epoch,
                ViveProEyeRehearsalCoordinates.MappingVersion,"vive-pro-eye-no-serial");
        }
        public static ViveLegacyWozProcessPairing CreateFreshParticipantOffer(ViveLegacyWozTaskOwner owner,Actor actor,string sourceOwner,string existingOperatorClockId)
        {return Create(owner,actor,sourceOwner,existingOperatorClockId,ControlSource.LiveOperator);}
        static ViveLegacyWozProcessPairing Create(ViveLegacyWozTaskOwner owner,Actor actor,string sourceOwner,string operatorClock,ControlSource mode)
        {
            if(owner==null||!owner.OnOwnerThread||owner.Terminal||actor!=Actor.Owner)throw new InvalidOperationException("Actual current participant owner required.");
            WireCodec.RequireIdentity(operatorClock);WireCodec.RequireIdentity(sourceOwner);
            if(operatorClock==owner.Clock.ClockId)throw new ArgumentException("Existing role clocks must have distinct IDs.");
            owner.ValidateLocalSourceSelection(actor,sourceOwner);
            byte[] key=new byte[32],epoch=new byte[16];ViveLegacyWozTaskOwner.SourceChoice choice=null;bool attemptedSelection=false;
            try
            {
                using(var rng=RandomNumberGenerator.Create()){rng.GetBytes(key);rng.GetBytes(epoch);}
                string label=BitConverter.ToString(epoch).Replace("-","");
                string stream=mode==ControlSource.Simulator?"simulator/legacy-process/"+label:
                    ViveDevelopmentTransport.OperatorStream(ViveProEyeRehearsalCoordinates.MappingVersion,label);
                attemptedSelection=true;choice=owner.SelectAndConfirmLocalSource(actor,sourceOwner,Convert.ToBase64String(epoch),stream,ViveProEyeRehearsalCoordinates.MappingVersion);
                bool recording=owner.Participant.LegacyWozRecordingDeclared;
                if(recording&&!owner.RecordingAdmissionCurrent)throw new InvalidOperationException("Declared WoZ recording authority was lost during selection");
                return new ViveLegacyWozProcessPairing(key,epoch,owner.SessionId,owner.Clock.ClockId,operatorClock,sourceOwner,choice.SourceGeneration,choice.Generation,mode,choice,recording);
            }
            catch{if(attemptedSelection){owner.RevokeSourceChoice(choice);if(!owner.PauseAuthoritativeTask())owner.MarkCleanupUncertain();owner.Dispose();}throw;}
            finally{Array.Clear(key,0,key.Length);Array.Clear(epoch,0,epoch.Length);}
        }
        byte[] Encode()
        {
            Check();using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,new UTF8Encoding(false,true)))
            {
                writer.Write((byte)(ParticipantLocalRecordingDeclared?2:1));writer.Write(key);writer.Write(epoch);writer.Write(Context.Participant.SessionId);
                writer.Write(Context.Participant.ClockId);writer.Write(Context.Operator.ClockId);writer.Write(SourceOwner);
                writer.Write(SourceGeneration);writer.Write(ChoiceGeneration);writer.Write((byte)Context.Operator.Source);
                writer.Flush();return stream.ToArray();
            }
        }
        public char[] RevealToken()
        {var bytes=Encode();try{return ((ParticipantLocalRecordingDeclared?RecordingPrefix:Prefix)+Convert.ToBase64String(bytes)).ToCharArray();}finally{Array.Clear(bytes,0,bytes.Length);}}
        public static ViveLegacyWozProcessPairing Parse(char[] token)
        {
            if(token==null||token.Length<Prefix.Length+64||token.Length>2048)throw new ArgumentException("Invalid process pairing token");
            bool recording=true,ordinary=true;
            for(int i=0;i<Prefix.Length;i++){recording&=token[i]==RecordingPrefix[i];ordinary&=token[i]==Prefix[i];}
            if(!recording&&!ordinary)throw new ArgumentException("Invalid process pairing version");
            byte[] bytes=null,key=null,epoch=null;ViveLegacyWozProcessPairing pairing=null;
            try
            {
                bytes=Convert.FromBase64CharArray(token,Prefix.Length,token.Length-Prefix.Length);
                using(var stream=new MemoryStream(bytes,false))using(var reader=new BinaryReader(stream,new UTF8Encoding(false,true)))
                {
                    if(reader.ReadByte()!=(recording?2:1))throw new FormatException();key=reader.ReadBytes(32);epoch=reader.ReadBytes(16);
                    string session=reader.ReadString(),pc=reader.ReadString(),oc=reader.ReadString(),owner=reader.ReadString();
                    long source=reader.ReadInt64(),choice=reader.ReadInt64();var mode=(ControlSource)reader.ReadByte();
                    if(stream.Position!=stream.Length)throw new FormatException();
                    pairing=new ViveLegacyWozProcessPairing(key,epoch,session,pc,oc,owner,source,choice,mode,null,recording);
                }
                char[] canonical=pairing.RevealToken();bool same=canonical.Length==token.Length;
                for(int i=0;same&&i<token.Length;i++)same=canonical[i]==token[i];Array.Clear(canonical,0,canonical.Length);
                if(!same)throw new FormatException();return pairing;
            }
            catch(Exception e) when(e is ArgumentException||e is FormatException||e is IOException)
            {if(pairing!=null)pairing.Dispose();throw new ArgumentException("Invalid canonical process pairing token");}
            finally{if(bytes!=null)Array.Clear(bytes,0,bytes.Length);if(key!=null)Array.Clear(key,0,key.Length);if(epoch!=null)Array.Clear(epoch,0,epoch.Length);}
        }
        internal ViveLegacyWozTaskOwner.SourceChoice ClaimParticipant(ViveLegacyWozTaskOwner owner)
        {
            Check();if(participantClaimed||owner==null||localChoice==null||!owner.IsSourceChoiceCurrent(localChoice)||
                ParticipantLocalRecordingDeclared!=owner.Participant.LegacyWozRecordingDeclared||(ParticipantLocalRecordingDeclared&&!owner.RecordingAdmissionCurrent)||
                owner.SessionId!=Context.Participant.SessionId||owner.Clock.ClockId!=Context.Participant.ClockId)
                throw new InvalidOperationException("Fresh creator-owned actual participant choice required; imports cannot create it.");
            participantClaimed=true;return localChoice;
        }
        internal void ClaimOperator(MonotonicClock clock)
        {
            Check();if(operatorClaimed||clock==null||clock.ClockId!=Context.Operator.ClockId)throw new InvalidOperationException("Existing operator clock does not match pairing.");
            lock(claimGate){if(spentOperatorEpochs.Count>=1024||!spentOperatorEpochs.Add(Context.Epoch))throw new InvalidOperationException("A fresh process pairing epoch is required.");}
            operatorClaimed=true;
        }
        // V1 remains byte compatible. V2 uses a distinct authenticated domain:
        // rewriting a recorded offer into an old/unrecorded token cannot create
        // a receipt accepted by the recorded participant. This is key possession,
        // not a claim that the remote person read a notice or is a given device.
        internal byte[] CopyKey()
        {Check();if(!ParticipantLocalRecordingDeclared)return (byte[])key.Clone();
            using(var mac=new HMACSHA256(key))return mac.ComputeHash(Encoding.ASCII.GetBytes("WOZPROC2/PARTICIPANT_LOCAL_RECORDING/v1"));}
        void Check(){if(disposed)throw new ObjectDisposedException("ProcessPairing");}
        public void Dispose()
        {
            if(disposed)return;
            if(localChoice!=null&&!participantClaimed)
            {
                if(!localChoice.Owner.OnOwnerThread)throw new InvalidOperationException("Unclaimed participant offer cancellation requires owner thread.");
                localChoice.Owner.RevokeSourceChoice(localChoice);
                if(!localChoice.Owner.PauseAuthoritativeTask())localChoice.Owner.MarkCleanupUncertain();localChoice.Owner.Dispose();
            }
            disposed=true;Array.Clear(key,0,key.Length);Array.Clear(epoch,0,epoch.Length);
        }
#if STROOP_TEST_BUILD
        internal static ViveLegacyWozProcessPairing CreateSyntheticOffer(ViveLegacyWozTaskOwner owner,string sourceOwner,string existingOperatorClockId)
        {return Create(owner,Actor.Owner,sourceOwner,existingOperatorClockId,ControlSource.Simulator);}
#endif
    }
}
