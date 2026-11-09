using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Opt-in engineering evidence only. No acquisition, protocol admission,
    // calculation of coverage, or linkage to existing Quest exports occurs here.
    public enum ViveDevelopmentDataSource { RealVive=1, SyntheticFixture=2 }
    public enum ViveDevelopmentGapReason { Missing=1, Stale=2, OutOfOrder=3, DeviceUnavailable=4, CalibrationUnavailable=5, RuntimeFailure=6, Other=7 }

    public sealed class ViveDevelopmentExportRecord
    {
        public ViveDevelopmentProvenance Provenance {get;private set;}
        public ViveDevelopmentDataSource Source {get;private set;}
        public long Ordinal {get;private set;}
        public int BlockIndex {get;private set;}
        public int Segment {get;private set;}
        public bool IsGap {get;private set;}
        public ViveDevelopmentGapReason? GapReason {get;private set;}
        public double? GapStartLocalSeconds {get;private set;}
        public double? GapEndLocalSeconds {get;private set;}
        public int? NativeFrameSequence {get;private set;}
        public ulong? NativeDirectionMask {get;private set;}
        public Vec3? RawSdkDirection {get;private set;}
        public Vec3? DerivedDirection {get;private set;}
        public string DerivedDirectionFrame {get;private set;}
        public bool? NativeDirectionReportedValid {get;private set;}
        public bool? EyeUsable {get;private set;}

        ViveDevelopmentExportRecord(ViveDevelopmentProvenance provenance,ViveDevelopmentDataSource source,long ordinal,int block,int segment)
        {
            if(provenance==null)throw new ArgumentNullException("provenance");
            if(!Enum.IsDefined(typeof(ViveDevelopmentDataSource),source))throw new ArgumentOutOfRangeException("source");
            if(ordinal<0||block<0||block>3||segment<0)throw new ArgumentOutOfRangeException("recordIdentity");
            Provenance=provenance;Source=source;Ordinal=ordinal;BlockIndex=block;Segment=segment;
        }
        public static ViveDevelopmentExportRecord Observation(ViveDevelopmentProvenance provenance,ViveDevelopmentDataSource source,
            long ordinal,int block,int segment,int nativeFrameSequence,ulong nativeDirectionMask,Vec3 rawSdkDirection,
            bool nativeDirectionReportedValid,Vec3? derivedDirection,string derivedDirectionFrame,bool eyeUsable)
        {
            var record=new ViveDevelopmentExportRecord(provenance,source,ordinal,block,segment);
            if(!provenance.EyeObservedLocalSeconds.HasValue||!provenance.NativeEyeTimestampOpaque.HasValue)
                throw new ArgumentException("An eye observation and opaque native timestamp are required");
            if(derivedDirection.HasValue&&!derivedDirection.Value.IsUnit)
                throw new ArgumentException("A derived direction must be finite and unit length", "derivedDirection");
            if(derivedDirection.HasValue!=!String.IsNullOrWhiteSpace(derivedDirectionFrame))
                throw new ArgumentException("Derived direction and explicit coordinate frame must be supplied together", "derivedDirectionFrame");
            if(derivedDirectionFrame!=null&&(derivedDirectionFrame.Length>256||derivedDirectionFrame!=derivedDirectionFrame.Trim()))
                throw new ArgumentException("Canonical derived direction frame required", "derivedDirectionFrame");
            if(derivedDirectionFrame!=null)foreach(char c in derivedDirectionFrame)if(Char.IsControl(c))
                throw new ArgumentException("Control characters are not allowed in a direction frame", "derivedDirectionFrame");
            Vec3 unused;
            if(eyeUsable&&(!nativeDirectionReportedValid||(nativeDirectionMask&2UL)==0||!ViveProEyeSdkDirection.TryGetUnitDirection(rawSdkDirection,out unused)||!derivedDirection.HasValue))
                throw new ArgumentException("Usable eye output requires native validity, finite raw direction and a derived unit direction");
            record.NativeFrameSequence=nativeFrameSequence;record.NativeDirectionMask=nativeDirectionMask;
            record.RawSdkDirection=rawSdkDirection;record.NativeDirectionReportedValid=nativeDirectionReportedValid;
            record.DerivedDirection=derivedDirection;record.DerivedDirectionFrame=derivedDirectionFrame;record.EyeUsable=eyeUsable;return record;
        }
        public static ViveDevelopmentExportRecord Gap(ViveDevelopmentProvenance provenance,ViveDevelopmentDataSource source,
            long ordinal,int block,int segment,double startLocalSeconds,double endLocalSeconds,ViveDevelopmentGapReason reason)
        {
            var record=new ViveDevelopmentExportRecord(provenance,source,ordinal,block,segment);
            if(Double.IsNaN(startLocalSeconds)||Double.IsInfinity(startLocalSeconds)||startLocalSeconds<0||
               Double.IsNaN(endLocalSeconds)||Double.IsInfinity(endLocalSeconds)||endLocalSeconds<=startLocalSeconds)
                throw new ArgumentOutOfRangeException("startLocalSeconds", "A finite positive-duration local gap is required");
            if(!Enum.IsDefined(typeof(ViveDevelopmentGapReason),reason))throw new ArgumentOutOfRangeException("reason");
            record.IsGap=true;record.GapReason=reason;record.GapStartLocalSeconds=startLocalSeconds;record.GapEndLocalSeconds=endLocalSeconds;return record;
        }
    }

    public static class ViveDevelopmentExport
    {
        public const string SchemaVersion="vive-development-eye-v1";
        // This initial schema records the native combined-eye direction only.
        // It does not claim to preserve eye origins, left/right rays, head pose,
        // responses or trial events; those need separately versioned extensions.
        public const string Header="schema_version,record_kind,data_source,session_id,participant_id,operator_id,measurement_role,stream_id,device_id,local_clock_id,mapping_version,hardware_capture_time_verified,apparatus_mapping_validated,record_ordinal,block_index,segment,poll_started_local_seconds,poll_completed_local_seconds,head_observed_local_seconds,eye_observed_local_seconds,native_eye_timestamp_opaque,native_frame_sequence,native_combined_mask,native_combined_direction_reported_valid,raw_combined_sdk_direction_x,raw_combined_sdk_direction_y,raw_combined_sdk_direction_z,derived_direction_frame,derived_direction_x,derived_direction_y,derived_direction_z,eye_usable,gap_start_local_seconds,gap_end_local_seconds,gap_reason";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly StringComparison PathComparison=Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;

        // The caller chooses an existing output directory and explicitly opts in.
        // No session/process is started here. A new unique name is required; existing
        // output is never overwritten. Temp file and final are on the same volume.
        public static string WriteNew(string directory,string exportId,IEnumerable<ViveDevelopmentExportRecord> records,bool explicitOptIn)
        {
            if(!explicitOptIn)throw new InvalidOperationException("Explicit development export opt-in is required");
            if(records==null)throw new ArgumentNullException("records");
            if(String.IsNullOrEmpty(exportId)||exportId.Length>80)throw new ArgumentException("Explicit safe export identity required", "exportId");
            foreach(char c in exportId)if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='-'||c=='_'))
                throw new ArgumentException("Export identity must use ASCII letters, digits, hyphen or underscore", "exportId");
            if(String.IsNullOrWhiteSpace(directory)||!Path.IsPathRooted(directory))throw new ArgumentException("An existing absolute output directory is required", "directory");
            string root=Path.GetFullPath(directory);
            if(!Directory.Exists(root))throw new DirectoryNotFoundException("Export directory does not exist");
            for(string path=root;path!=null;path=Directory.GetParent(path)==null?null:Directory.GetParent(path).FullName)
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked export directories are not supported");
            string final=Path.Combine(root,"vive-development-"+exportId+".csv");
            if(!String.Equals(Path.GetDirectoryName(final),root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),PathComparison) &&
               !String.Equals(Path.GetDirectoryName(final),root,PathComparison))throw new IOException("Export escaped its output directory");
            string temporary=Path.Combine(root,".vive-development-"+Guid.NewGuid().ToString("N")+".partial");
            bool created=false;
            try
            {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    created=true;
                    using(var writer=new StreamWriter(stream,Utf8,4096,true))
                    {
                        writer.NewLine="\n";writer.WriteLine(Header);
                        ViveDevelopmentExportRecord first=null;long expected=0;
                        foreach(var record in records)
                        {
                            if(record==null)throw new InvalidDataException("Null export record");
                            RequireLegacyRecord(record);
                            if(record.Ordinal!=expected)throw new InvalidDataException("Record ordinals must be contiguous from zero; provide explicit gaps");
                            if(first==null)first=record;
                            else if(!SameStream(first,record))throw new InvalidDataException("One export must contain one explicit stream epoch and source");
                            writer.WriteLine(Row(record));expected=checked(expected+1);
                        }
                        if(first==null)throw new InvalidDataException("No development evidence to export");
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                // File.Move never replaces an existing destination. Failed writes or
                // interrupted enumeration leave no apparently completed CSV.
                File.Move(temporary,final);created=false;return final;
            }
            finally
            {
                if(created&&File.Exists(temporary))File.Delete(temporary);
            }
        }
        // Kept separate so schema downgrade rejection can be tested without a
        // filesystem action. The v2 journal has its own versioned extractor.
        internal static void RequireLegacyRecord(ViveDevelopmentExportRecord record)
        {
            if(record==null)throw new InvalidDataException("Null export record");
            if(record.Provenance.IsVersionTwoIdentity)throw new InvalidDataException("V2 observations require a versioned v2 extractor; v1 CSV cannot represent operator status");
        }
        static bool SameStream(ViveDevelopmentExportRecord a,ViveDevelopmentExportRecord b)
        {
            var x=a.Provenance;var y=b.Provenance;
            return a.Source==b.Source&&x.IsVersionTwoIdentity==y.IsVersionTwoIdentity&&x.OperatorStatus==y.OperatorStatus&&x.MeasurementRole==y.MeasurementRole&&x.SessionId==y.SessionId&&
                x.ParticipantId==y.ParticipantId&&x.OperatorId==y.OperatorId&&x.StreamId==y.StreamId&&
                x.DeviceId==y.DeviceId&&x.LocalObservationClockId==y.LocalObservationClockId&&x.MappingVersion==y.MappingVersion;
        }
        static string Row(ViveDevelopmentExportRecord r)
        {
            var p=r.Provenance;
            var fields=new List<string>{SchemaVersion,r.IsGap?"gap":"observation",r.Source.ToString(),p.SessionId,p.ParticipantId,p.OperatorId,
                p.MeasurementRole.ToString(),p.StreamId,p.DeviceId,p.LocalObservationClockId,p.MappingVersion,"false","false",N(r.Ordinal),N(r.BlockIndex),N(r.Segment),
                N(p.PollStartedLocalSeconds),N(p.PollCompletedLocalSeconds),N(p.HeadObservedLocalSeconds),r.IsGap?"":N(p.EyeObservedLocalSeconds),
                r.IsGap?"":N(p.NativeEyeTimestampOpaque),N(r.NativeFrameSequence),N(r.NativeDirectionMask),B(r.NativeDirectionReportedValid)};
            AddVector(fields,r.RawSdkDirection);fields.Add(r.DerivedDirectionFrame??"");AddVector(fields,r.DerivedDirection);
            fields.Add(B(r.EyeUsable));fields.Add(N(r.GapStartLocalSeconds));fields.Add(N(r.GapEndLocalSeconds));fields.Add(r.GapReason.HasValue?r.GapReason.Value.ToString():"");
            // Reject dangerous spreadsheet-leading identity cells without changing
            // their raw values; numeric fields, including negative opaque times, stay numeric.
            for(int i=3;i<=10;i++)SafeText(fields[i]);
            SafeText(r.DerivedDirectionFrame);
            return String.Join(",",fields.ConvertAll(Csv).ToArray());
        }
        static void AddVector(List<string> result,Vec3? v)
        {result.Add(v.HasValue?N(v.Value.X):"");result.Add(v.HasValue?N(v.Value.Y):"");result.Add(v.HasValue?N(v.Value.Z):"");}
        static string N<T>(T? value) where T:struct,IFormattable {return value.HasValue?N(value.Value):"";}
        static string N<T>(T value) where T:struct,IFormattable {return value.ToString(value is double?"R":null,CultureInfo.InvariantCulture);}
        static string B(bool? value){return value.HasValue?(value.Value?"true":"false"):"";}
        static string Csv(string value){return "\""+(value??"").Replace("\"","\"\"")+"\"";}
        static void SafeText(string value)
        {if(!String.IsNullOrEmpty(value)&&(value[0]=='='||value[0]=='+'||value[0]=='-'||value[0]=='@'))throw new InvalidDataException("Unsafe spreadsheet identity prefix");}
    }
}
