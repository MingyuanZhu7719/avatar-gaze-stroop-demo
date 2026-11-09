using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // The stage calls each verification under its own distinct I/O claim.
    // None of these checks grants consent or proves later path identity.
    internal interface IViveRawStageTargetVerification
    {
        bool VerifyFileType();
        bool VerifyFileIdentity();
        bool VerifyFileInformation();
    }

    // Wraps one already-open exclusive synchronous target; identity is
    // verified by the separate ordered checks before data operations.
    // It neither opens a path nor grants consent, source or publication rights.
    // Its owner must fence each method/property call against the shared stage
    // withdrawal authority. In particular, do not call Dispose until the
    // stage worker has exited and the outer close claim has been obtained.
    internal sealed class ViveRawSiblingStream:Stream,IViveRawStageTargetVerification
    {
        internal enum Operation { None, VerifyType, VerifyIdentity, VerifyInformation, Read, Write, Length, SeekStart, Flush, Close }
        static readonly object RetentionGate=new object();
        static readonly List<ViveRawSiblingStream> Retained=new List<ViveRawSiblingStream>();
        readonly ViveRawRelativeFileCapability.RawNativeHandle handle;
        readonly Func<ulong,byte[],bool> identityMatches;
        int active,uncertain,closed,lastError,verificationState;long position;
        Operation lastOperation;
        byte[] inFlight;GCHandle pin;bool pinned;
        ViveRawNativeOutputStorage nativeCount;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal Action BeforeWriteForTest;
        internal Action BeforeVerifyFileTypeForTest;
        internal Action BeforeCloseForTest;
#endif

        // The factory must transfer one handle, not a second owning wrapper
        // around the source handle. The identity matcher must use pre-observed
        // data only; it must not start any additional OS operation.
        internal ViveRawSiblingStream(ViveRawRelativeFileCapability.RawNativeHandle value,Func<ulong,byte[],bool> expectedIdentity)
        {
            if(value==null||value.IsClosed||value.IsInvalid||expectedIdentity==null)throw new ArgumentException("A valid exclusive target and identity check are required.");
            handle=value;identityMatches=expectedIdentity;
        }
        internal bool OutcomeUnknown {get{return Volatile.Read(ref uncertain)!=0;}}
        internal bool CloseReturned {get{return Volatile.Read(ref closed)!=0;}}
        internal Operation LastOperation {get{return lastOperation;}}
        internal int? LastNativeError {get{int value=Volatile.Read(ref lastError);return value==0?(int?)null:value;}}
        internal static int RetainedCount {get{lock(RetentionGate)return Retained.Count;}}

        void Keep(){lock(RetentionGate)if(!Retained.Contains(this))Retained.Add(this);}
        void ReleaseIfIdle()
        {
            lock(RetentionGate)
                if(Volatile.Read(ref active)==0&&!OutcomeUnknown)Retained.Remove(this);
        }
        void MarkUnknown(int error=0)
        {if(error!=0)Volatile.Write(ref lastError,error);Interlocked.Exchange(ref uncertain,1);Keep();}
        void Begin(Operation operation)
        {
            if(OutcomeUnknown)throw new IOException("Target I/O outcome is unknown.");
            if(Volatile.Read(ref closed)!=0)throw new ObjectDisposedException("ViveRawSiblingStream");
            if(Interlocked.CompareExchange(ref active,1,0)!=0){MarkUnknown();throw new IOException("Concurrent target I/O is unavailable.");}
            if(OutcomeUnknown||Volatile.Read(ref closed)!=0){Interlocked.Exchange(ref active,0);throw new IOException("Target I/O is unavailable.");}
            lastOperation=operation;Volatile.Write(ref lastError,0);
            // Root the handle and any native output storage before entering an
            // OS operation; a non-returning or uncertain operation must not
            // become a finalizer-driven close or a freed output buffer.
            Keep();
        }
        void Finish(bool returned,bool succeeded)
        {
            if(!returned||!succeeded||OutcomeUnknown)MarkUnknown();
            Interlocked.Exchange(ref active,0);
            if(returned&&succeeded)ReleaseIfIdle();
        }
        void ReleaseBuffer()
        {
            if(inFlight!=null)Array.Clear(inFlight,0,inFlight.Length);
            if(pinned){pin.Free();pinned=false;}
            if(nativeCount!=null){nativeCount.ReleaseAfterConfirmedCompletion();nativeCount=null;}
            inFlight=null;
        }
        static void Validate(byte[] buffer,int offset,int count)
        {if(buffer==null)throw new ArgumentNullException("buffer");if(offset<0||count<0||offset>buffer.Length-count)throw new ArgumentOutOfRangeException();}
        void RequireVerified()
        {if(Volatile.Read(ref verificationState)!=3)throw new IOException("Target verification is incomplete.");}
        public bool VerifyFileType()
        {
            Begin(Operation.VerifyType);bool returned=false,success=false;
            try{if(Volatile.Read(ref verificationState)!=0){returned=true;return false;}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                var hook=BeforeVerifyFileTypeForTest;if(hook!=null)hook();
#endif
                // FILE_TYPE_UNKNOWN may require a separately cleared last-error
                // value to distinguish API failure from an unknown type.
                // Keep this as a validation failure, not an asserted OS code.
                uint type=ViveRawRelativeFileCapability.Native.GetFileType(handle);returned=true;success=type==1;
                if(success)Volatile.Write(ref verificationState,1);}
            finally{Finish(returned,success);}
            return success&&!OutcomeUnknown;
        }
        public bool VerifyFileIdentity()
        {
            Begin(Operation.VerifyIdentity);bool returned=false,success=false;
            try
            {
                if(Volatile.Read(ref verificationState)!=1){returned=true;return false;}
                ViveRawRelativeFileCapability.Native.FileId id;
                // GetId combines the native BOOL with structure validation;
                // a false result does not establish which one failed.
                bool obtained=ViveRawRelativeFileCapability.Native.GetId(handle,out id);
                returned=true;
                success=obtained&&id.Id!=null&&id.Id.Length==16&&identityMatches(id.Volume,id.Id);
                if(success)Volatile.Write(ref verificationState,2);
            }
            finally{Finish(returned,success);}
            return success&&!OutcomeUnknown;
        }
        public bool VerifyFileInformation()
        {
            Begin(Operation.VerifyInformation);bool returned=false,success=false;
            try
            {
                if(Volatile.Read(ref verificationState)!=2){returned=true;return false;}
                ViveRawRelativeFileCapability.Native.FileInformation info;
                bool obtained=ViveRawRelativeFileCapability.Native.GetInfo(handle,out info);int error=obtained?0:Marshal.GetLastWin32Error();
                returned=true;if(error!=0)Volatile.Write(ref lastError,error);
                success=obtained&&(info.Attributes&0x410)==0;
                if(success)Volatile.Write(ref verificationState,3);
            }
            finally{Finish(returned,success);}
            return success&&!OutcomeUnknown;
        }
        public override bool CanRead {get{return Volatile.Read(ref verificationState)==3&&!OutcomeUnknown&&Volatile.Read(ref closed)==0;}}
        public override bool CanWrite {get{return Volatile.Read(ref verificationState)==3&&!OutcomeUnknown&&Volatile.Read(ref closed)==0;}}
        public override bool CanSeek {get{return Volatile.Read(ref verificationState)==3&&!OutcomeUnknown&&Volatile.Read(ref closed)==0;}}
        public override long Length
        {
            get
            {
                RequireVerified();Begin(Operation.Length);bool returned=false,success=false;long value=0;
                try{success=ViveRawRelativeFileCapability.Native.GetSize(handle,out value);int error=success?0:Marshal.GetLastWin32Error();returned=true;if(error!=0)Volatile.Write(ref lastError,error);}
                finally{Finish(returned,success);}
                if(!success||OutcomeUnknown)throw new IOException("Target size outcome is unknown.");
                return value;
            }
        }
        public override long Position
        {
            get{RequireVerified();if(OutcomeUnknown)throw new IOException("Target position is unknown.");if(Volatile.Read(ref closed)!=0)throw new ObjectDisposedException("ViveRawSiblingStream");return position;}
            set{if(value!=0)throw new NotSupportedException("Only the start position is supported.");Seek(0,SeekOrigin.Begin);}
        }
        public override long Seek(long offset,SeekOrigin origin)
        {
            if(offset!=0||origin!=SeekOrigin.Begin)throw new NotSupportedException("Only seek to start is supported.");
            RequireVerified();Begin(Operation.SeekStart);bool returned=false,success=false;
            try{success=ViveRawRelativeFileCapability.Native.SeekStart(handle);int error=success?0:Marshal.GetLastWin32Error();returned=true;if(error!=0)Volatile.Write(ref lastError,error);}
            finally{Finish(returned,success);}
            if(!success||OutcomeUnknown)throw new IOException("Target seek outcome is unknown.");position=0;return 0;
        }
        public override void SetLength(long value){throw new NotSupportedException();}
        public override void Flush()
        {
            RequireVerified();Begin(Operation.Flush);bool returned=false,success=false;
            try{success=ViveRawRelativeFileCapability.Native.Flush(handle);int error=success?0:Marshal.GetLastWin32Error();returned=true;if(error!=0)Volatile.Write(ref lastError,error);}
            finally{Finish(returned,success);}
            if(!success||OutcomeUnknown)throw new IOException("Target flush outcome is unknown.");
        }
        public override int Read(byte[] buffer,int offset,int count)
        {
            Validate(buffer,offset,count);RequireVerified();if(count==0)return 0;
            int requested=Math.Min(count,65536);Begin(Operation.Read);bool attempted=false,returned=false,success=false;uint actual=0;
            try
            {
                inFlight=new byte[requested];pin=GCHandle.Alloc(inFlight,GCHandleType.Pinned);pinned=true;
                nativeCount=new ViveRawNativeOutputStorage(sizeof(uint));attempted=true;
                success=ViveRawRelativeFileCapability.Native.Read(handle,pin.AddrOfPinnedObject(),(uint)requested,nativeCount.Address);
                int error=success?0:Marshal.GetLastWin32Error();returned=true;if(error!=0)Volatile.Write(ref lastError,error);
                if(success){actual=unchecked((uint)Marshal.ReadInt32(nativeCount.Address));success=actual<=(uint)requested;
                    if(success&&!OutcomeUnknown)Buffer.BlockCopy(inFlight,0,buffer,offset,(int)actual);}
            }
            finally
            {
                if(!attempted||(returned&&success&&!OutcomeUnknown))ReleaseBuffer();
                Finish(returned,success);
            }
            if(!success||OutcomeUnknown)throw new IOException("Target read outcome is unknown.");
            position=checked(position+actual);return (int)actual;
        }
        public override void Write(byte[] buffer,int offset,int count)
        {
            Validate(buffer,offset,count);RequireVerified();if(count==0)return;
            if(count>ViveRawRelativeFileCapability.MaximumWriteBytes)throw new ArgumentOutOfRangeException("count");
            Begin(Operation.Write);bool attempted=false,returned=false,success=false;uint actual=0;
            try
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                var hook=BeforeWriteForTest;if(hook!=null)hook();
#endif
                inFlight=new byte[count];Buffer.BlockCopy(buffer,offset,inFlight,0,count);
                pin=GCHandle.Alloc(inFlight,GCHandleType.Pinned);pinned=true;
                nativeCount=new ViveRawNativeOutputStorage(sizeof(uint));attempted=true;
                success=ViveRawRelativeFileCapability.Native.Write(handle,pin.AddrOfPinnedObject(),(uint)count,nativeCount.Address);
                int error=success?0:Marshal.GetLastWin32Error();returned=true;if(error!=0)Volatile.Write(ref lastError,error);
                if(success){actual=unchecked((uint)Marshal.ReadInt32(nativeCount.Address));success=actual==(uint)count;}
            }
            finally
            {
                if(!attempted||(returned&&success&&!OutcomeUnknown))ReleaseBuffer();
                Finish(returned,success);
            }
            if(!success||OutcomeUnknown)throw new IOException("Target write outcome is unknown.");position=checked(position+actual);
        }
        protected override void Dispose(bool disposing)
        {
            if(!disposing)return;
            if(Volatile.Read(ref closed)!=0)return;
            Begin(Operation.Close);bool returned=false,success=false;
            try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                var hook=BeforeCloseForTest;if(hook!=null)hook();
#endif
                handle.Dispose();returned=true;success=handle.CloseOutcome==1;if(success)Volatile.Write(ref closed,1);}
            finally{Finish(returned,success);}
            if(!success||OutcomeUnknown)throw new IOException("Target close outcome is unknown.");
            base.Dispose(disposing);
        }
    }
}
