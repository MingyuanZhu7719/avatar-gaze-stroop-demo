using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentExportTests
    {
        static ViveDevelopmentProvenance Provenance(string session="synthetic-session",string stream="fixture/participant/1",string participant="participant,\"one\"")
        {return new ViveDevelopmentProvenance(session,participant,"operator",ViveDevelopmentMeasurementRole.Participant,stream,"device","clock","mapping-v3",-123,10,11,9.8,10.2,true);}
        static ViveDevelopmentExportRecord Sample(long ordinal=0,ViveDevelopmentProvenance provenance=null)
        {return ViveDevelopmentExportRecord.Observation(provenance??Provenance(),ViveDevelopmentDataSource.SyntheticFixture,ordinal,0,0,99,2,new Vec3(0,0,2),true,new Vec3(0,0,1),"tracking-local-rehearsal",true);}
        static void Temporary(Action<string> action)
        {
            string root=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string directory=Path.Combine(root,"vive-export-test-"+Guid.NewGuid().ToString("N"));
            if(!Path.GetFullPath(directory).StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Test directory escaped temporary root");
            Directory.CreateDirectory(directory);
            try{action(directory);}finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
        }
        static List<string> Parse(string line)
        {
            var result=new List<string>();var value=new StringBuilder();bool quoted=false;
            for(int i=0;i<line.Length;i++)
            {
                char c=line[i];
                if(c=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){value.Append('"');i++;}else quoted=!quoted;}
                else if(c==','&&!quoted){result.Add(value.ToString());value.Length=0;}else value.Append(c);
            }
            if(quoted)throw new InvalidDataException("Unterminated quoted CSV field");result.Add(value.ToString());return result;
        }
        static Dictionary<string,string> Fields(string header,string row)
        {
            string[] names=header.Split(',');var values=Parse(row);Check.Equal(names.Length,values.Count);
            var result=new Dictionary<string,string>();for(int i=0;i<names.Length;i++)result.Add(names[i],values[i]);return result;
        }
        static IEnumerable<ViveDevelopmentExportRecord> Interrupted()
        {yield return Sample();throw new IOException("synthetic interruption");}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE development export preserves raw derived identity opaque time and explicit gaps",()=>Temporary(root=>{
                var sample=Sample();var gap=ViveDevelopmentExportRecord.Gap(Provenance(),ViveDevelopmentDataSource.SyntheticFixture,1,0,0,10.3,10.4,ViveDevelopmentGapReason.Stale);
                string path=ViveDevelopmentExport.WriteNew(root,"synthetic-1",new[]{sample,gap},true);
                string[] lines=File.ReadAllLines(path);Check.Equal(3,lines.Length);Check.Equal(ViveDevelopmentExport.Header,lines[0]);
                var row=Fields(lines[0],lines[1]);Check.Equal(ViveDevelopmentExport.SchemaVersion,row["schema_version"]);
                Check.Equal("observation",row["record_kind"]);Check.Equal("SyntheticFixture",row["data_source"]);
                Check.Equal("participant,\"one\"",row["participant_id"]);Check.Equal("-123",row["native_eye_timestamp_opaque"]);
                Check.Equal("99",row["native_frame_sequence"]);Check.Equal("2",row["native_combined_mask"]);
                Check.Equal("2",row["raw_combined_sdk_direction_z"]);Check.Equal("1",row["derived_direction_z"]);
                Check.Equal("tracking-local-rehearsal",row["derived_direction_frame"]);Check.Equal("true",row["eye_usable"]);
                Check.Equal("false",row["hardware_capture_time_verified"]);Check.Equal("false",row["apparatus_mapping_validated"]);
                var missing=Fields(lines[0],lines[2]);Check.Equal("gap",missing["record_kind"]);Check.Equal("Stale",missing["gap_reason"]);
                Check.Equal("",missing["eye_observed_local_seconds"]);Check.Equal("",missing["native_eye_timestamp_opaque"]);
                Check.Equal("",missing["eye_usable"]);Check.Equal("10.3",missing["gap_start_local_seconds"]);
                Check.Equal(1,Directory.GetFiles(root).Length);
            }));
            suite.Run("VIVE development export needs explicit opt-in safe name and existing absolute directory",()=>Temporary(root=>{
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentExport.WriteNew(root,"x",new[]{Sample()},false));
                foreach(string bad in new[]{null,"","../outside","a/b","a\\b",new string('a',81)})
                    Check.Throws<ArgumentException>(()=>ViveDevelopmentExport.WriteNew(root,bad,new[]{Sample()},true));
                Check.Throws<ArgumentException>(()=>ViveDevelopmentExport.WriteNew("relative","x",new[]{Sample()},true));
                Check.Throws<DirectoryNotFoundException>(()=>ViveDevelopmentExport.WriteNew(Path.Combine(root,"missing"),"x",new[]{Sample()},true));
                Check.Equal(0,Directory.GetFiles(root).Length);
            }));
            suite.Run("VIVE development export never overwrites existing evidence",()=>Temporary(root=>{
                string path=ViveDevelopmentExport.WriteNew(root,"same",new[]{Sample()},true);byte[] original=File.ReadAllBytes(path);
                Check.Throws<IOException>(()=>ViveDevelopmentExport.WriteNew(root,"same",new[]{Sample()},true));
                Check.True(Convert.ToBase64String(original)==Convert.ToBase64String(File.ReadAllBytes(path)));Check.Equal(1,Directory.GetFiles(root).Length);
            }));
            suite.Run("VIVE development export interruption and invalid stream leave no completed or partial evidence",()=>Temporary(root=>{
                Check.Throws<IOException>(()=>ViveDevelopmentExport.WriteNew(root,"interrupted",Interrupted(),true));
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentExport.WriteNew(root,"empty",new ViveDevelopmentExportRecord[0],true));
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentExport.WriteNew(root,"order",new[]{Sample(1)},true));
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentExport.WriteNew(root,"mixed",new[]{Sample(),Sample(1,Provenance(stream:"another-stream"))},true));
                Check.Equal(0,Directory.GetFiles(root).Length);
            }));
            suite.Run("VIVE development export refuses invented valid eyes and mismatched coordinate frames",()=>{
                var p=Provenance();var source=ViveDevelopmentDataSource.RealVive;
                Check.Throws<ArgumentException>(()=>ViveDevelopmentExportRecord.Observation(p,source,0,0,0,1,0,new Vec3(0,0,1),true,new Vec3(0,0,1),"tracking-local",true));
                Check.Throws<ArgumentException>(()=>ViveDevelopmentExportRecord.Observation(p,source,0,0,0,1,2,new Vec3(0,0,0),true,new Vec3(0,0,1),"tracking-local",true));
                Check.Throws<ArgumentException>(()=>ViveDevelopmentExportRecord.Observation(p,source,0,0,0,1,2,new Vec3(0,0,1),true,new Vec3(0,0,2),"tracking-local",true));
                Check.Throws<ArgumentException>(()=>ViveDevelopmentExportRecord.Observation(p,source,0,0,0,1,2,new Vec3(0,0,1),true,new Vec3(0,0,1),null,true));
                var invalid=ViveDevelopmentExportRecord.Observation(p,source,0,0,0,1,0,new Vec3(Double.NaN,0,1),false,null,null,false);
                Check.True(!invalid.EyeUsable.Value&&Double.IsNaN(invalid.RawSdkDirection.Value.X));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveDevelopmentExportRecord.Gap(p,source,0,0,0,2,2,ViveDevelopmentGapReason.Stale));
            });
            suite.Run("VIVE development export blocks spreadsheet formulas in text identities without mutating data",()=>Temporary(root=>{
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentExport.WriteNew(root,"unsafe",new[]{Sample(0,Provenance(participant:"=malicious"))},true));
                Check.Equal(0,Directory.GetFiles(root).Length);
            }));
        }
    }
}
