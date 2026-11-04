using System;
using System.IO;
using System.Linq;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class PreviewExportReceiptTests
    {
        const string Session="test-session",First="0123456789abcdef0123456789abcdef",Second="1123456789abcdef0123456789abcdef";
        static void Temporary(Action<string> action)
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            // Framework's temporary root is already nested under build evidence.
            // Reserve a compact name exclusively; never reuse a colliding path.
            string reservation,root;FileStream claim;
            while(true)
            {
                reservation=Path.Combine(parent,"xr-"+Guid.NewGuid().ToString("N").Substring(0,12));root=reservation+".d";
                if(File.Exists(root)||Directory.Exists(root))continue;
                try{claim=new FileStream(reservation,FileMode.CreateNew,FileAccess.Write,FileShare.None);break;}
                catch(IOException){if(!File.Exists(reservation))throw;}
            }
            bool created=false;
            try
            {
                if(File.Exists(root)||Directory.Exists(root))throw new IOException("Synthetic directory appeared after reservation; refusing reuse");
                Directory.CreateDirectory(root);created=true;action(root);
            }
            finally
            {
                try
                {
                    string full=Path.GetFullPath(root);
                    Check.True(full.StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("xr-",StringComparison.Ordinal)&&full==reservation+".d");
                    if(created&&Directory.Exists(full))Directory.Delete(full,true);
                }
                finally{claim.Dispose();File.Delete(reservation);}
            }
        }
        static ExportResult Export(string root,string parent=null)
        {
            var experiment=Fixtures.New(new ManualClock());
            var result=AuditExporter.Export(experiment,new AuditLog(Session,RunKind.Simulation),parent??Path.Combine(root,"exports"),
                new ExportProvenance("receipt-fixture","synthetic presentation only","synthetic input only"));
            Check.True(result.Success,result.Error);return result;
        }
        static PreviewExportReceipt Read(string root,string request=First)
        {
            PreviewExportReceipt receipt;string reason;
            Check.True(PreviewExportReceipt.TryReadVerified(root,Session,request,out receipt,out reason),reason);return receipt;
        }
        static void Refused(string root,string session=Session,string request=First)
        {
            PreviewExportReceipt receipt;string reason;
            Check.True(!PreviewExportReceipt.TryReadVerified(root,session,request,out receipt,out reason));
            Check.Equal<PreviewExportReceipt>(null,receipt);Check.True(!String.IsNullOrEmpty(reason));
        }
        static void RewriteField(string root,int field,string value)
        {
            string path=PreviewExportReceipt.ReceiptPath(root,First);var fields=File.ReadAllText(path).TrimEnd('\n').Split('|');
            fields[field]=value;File.WriteAllText(path,String.Join("|",fields)+"\n",new UTF8Encoding(false));
        }
        public static void Register(TestSuite t)
        {
            t.Run("Export receipt identifies a complete partial-session snapshot without changing CSV bytes",()=>Temporary(root=>{
                var result=Export(root);Check.True(!result.SessionCompleted);
                byte[] before=File.ReadAllBytes(Path.Combine(result.DirectoryPath,"manifest.csv"));
                PreviewExportReceipt.WriteCompleted(root,Session,First,result);
                var receipt=Read(root);Check.Equal(result.DirectoryPath,receipt.DirectoryPath);Check.Equal(Session,receipt.SessionId);Check.Equal(First,receipt.RequestId);
                Check.Equal(Convert.ToBase64String(before),Convert.ToBase64String(File.ReadAllBytes(Path.Combine(result.DirectoryPath,"manifest.csv"))));
            }));
            t.Run("Export receipt rejects missing malformed truncated and unknown-version evidence",()=>Temporary(root=>{
                Refused(root);string path=PreviewExportReceipt.ReceiptPath(root,First);
                foreach(string invalid in new[]{"", "garbage\n","ASPREVIEWEXPORT|1|incomplete",new string('x',16385)})
                {File.WriteAllText(path,invalid);Refused(root);}
                File.Delete(path);PreviewExportReceipt.WriteCompleted(root,Session,First,Export(root));RewriteField(root,1,"2");Refused(root);
            }));
            t.Run("Export receipt rejects mismatched session request and noncanonical request paths",()=>Temporary(root=>{
                PreviewExportReceipt.WriteCompleted(root,Session,First,Export(root));Refused(root,"another-session");Refused(root,Session,Second);
                Check.Throws<ArgumentException>(()=>PreviewExportReceipt.ReceiptPath(root,"../outside"));
                Check.Throws<ArgumentException>(()=>PreviewExportReceipt.ReceiptPath("relative",First));
                RewriteField(root,3,Second);Refused(root);
            }));
            t.Run("Export receipt refuses another session's valid bundle and failed export results",()=>Temporary(root=>{
                var result=Export(root);
                Check.Throws<InvalidDataException>(()=>PreviewExportReceipt.WriteCompleted(root,"another-session",First,result));
                Check.Throws<InvalidOperationException>(()=>PreviewExportReceipt.WriteCompleted(root,Session,First,new ExportResult{Success=false,DirectoryPath=result.DirectoryPath}));
                Check.True(!File.Exists(PreviewExportReceipt.ReceiptPath(root,First)));
                string blocked=Path.Combine(root,"blocked-parent");File.WriteAllText(blocked,"synthetic obstacle");
                var failed=AuditExporter.Export(Fixtures.New(new ManualClock()),new AuditLog(Session,RunKind.Simulation),blocked,new ExportProvenance("fixture","synthetic","synthetic"));
                Check.True(!failed.Success);Check.Throws<InvalidOperationException>(()=>PreviewExportReceipt.WriteCompleted(root,Session,First,failed));Refused(root);
            }));
            t.Run("Export receipt rejects folders outside its session and path substitutions",()=>Temporary(root=>{
                var outside=Export(root,Path.Combine(root,"outside","exports"));
                Check.Throws<InvalidDataException>(()=>PreviewExportReceipt.WriteCompleted(root,Session,First,outside));
                var result=Export(root);PreviewExportReceipt.WriteCompleted(root,Session,First,result);
                RewriteField(root,4,Convert.ToBase64String(Encoding.UTF8.GetBytes(outside.DirectoryPath)));Refused(root);
                RewriteField(root,4,Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.Combine(result.DirectoryPath,"..",Path.GetFileName(result.DirectoryPath)))));Refused(root);
            }));
            t.Run("Export receipt refuses missing changed or unfinished CSV files",()=>Temporary(root=>{
                var result=Export(root);PreviewExportReceipt.WriteCompleted(root,Session,First,result);
                string file=Path.Combine(result.DirectoryPath,"ratings.csv");byte[] original=File.ReadAllBytes(file);
                File.Delete(file);Refused(root);File.WriteAllBytes(file,original);
                File.AppendAllText(file,"changed");Refused(root);File.WriteAllBytes(file,original);
                string partial=Path.Combine(result.DirectoryPath,"unexpected.csv.partial");File.WriteAllText(partial,"unfinished");Refused(root);
                File.Delete(partial);Read(root);
            }));
            t.Run("Export receipt rejects a changed or incomplete manifest before publication",()=>Temporary(root=>{
                var result=Export(root);string manifest=Path.Combine(result.DirectoryPath,"manifest.csv");string original=File.ReadAllText(manifest);
                File.WriteAllText(manifest,original.Replace(",true,false,",",false,false,"));
                Check.Throws<InvalidDataException>(()=>PreviewExportReceipt.WriteCompleted(root,Session,First,result));Refused(root);
                File.WriteAllText(manifest,original);PreviewExportReceipt.WriteCompleted(root,Session,First,result);
                File.WriteAllText(manifest,original.Replace("receipt-fixture","changed-fixture"));Refused(root);
            }));
            t.Run("Repeated exports retain two distinct complete folders and exact request receipts",()=>Temporary(root=>{
                var first=Export(root);PreviewExportReceipt.WriteCompleted(root,Session,First,first);
                byte[] before=File.ReadAllBytes(PreviewExportReceipt.ReceiptPath(root,First));
                var second=Export(root);PreviewExportReceipt.WriteCompleted(root,Session,Second,second);
                Check.True(first.DirectoryPath!=second.DirectoryPath);Check.Equal(first.DirectoryPath,Read(root).DirectoryPath);Check.Equal(second.DirectoryPath,Read(root,Second).DirectoryPath);
                Check.Equal(Convert.ToBase64String(before),Convert.ToBase64String(File.ReadAllBytes(PreviewExportReceipt.ReceiptPath(root,First))));
            }));
            t.Run("Export receipt publication cannot overwrite a previous successful request",()=>Temporary(root=>{
                var first=Export(root);PreviewExportReceipt.WriteCompleted(root,Session,First,first);
                var second=Export(root);byte[] manifest=File.ReadAllBytes(Path.Combine(second.DirectoryPath,"manifest.csv"));
                try{PreviewExportReceipt.WriteCompleted(root,Session,First,second);throw new Exception("Expected duplicate receipt rejection");}
                catch(IOException error){Check.True(error.Message.Contains(second.DirectoryPath)&&error.Message.Contains("publication failed; no automatic retry"));Check.True(error.InnerException!=null);}
                Check.Equal(Convert.ToBase64String(manifest),Convert.ToBase64String(File.ReadAllBytes(Path.Combine(second.DirectoryPath,"manifest.csv"))));
                Check.Equal(first.DirectoryPath,Read(root).DirectoryPath);
            }));
            t.Run("Blocked export receipt publication reports the preserved CSV path without a verified receipt",()=>Temporary(root=>{
                var result=Export(root);string path=PreviewExportReceipt.ReceiptPath(root,First);Directory.CreateDirectory(path);
                byte[] manifest=File.ReadAllBytes(Path.Combine(result.DirectoryPath,"manifest.csv"));
                try{PreviewExportReceipt.WriteCompleted(root,Session,First,result);throw new Exception("Expected blocked receipt rejection");}
                catch(IOException error){Check.True(error.Message.Contains("CSV bundle saved at "+result.DirectoryPath)&&error.Message.Contains("publication failed; no automatic retry"));Check.True(error.InnerException!=null);}
                Check.Equal(Convert.ToBase64String(manifest),Convert.ToBase64String(File.ReadAllBytes(Path.Combine(result.DirectoryPath,"manifest.csv"))));
                Check.True(!File.Exists(path));Refused(root);
            }));
            t.Run("Export receipt preserves Unicode folder paths and quoted manifest identity fields",()=>Temporary(root=>{
                string unicode=Path.Combine(root,"records-\u00e9-\u4e2d");Directory.CreateDirectory(unicode);
                var result=Export(unicode);PreviewExportReceipt.WriteCompleted(unicode,Session,First,result);Check.Equal(result.DirectoryPath,Read(unicode).DirectoryPath);
                string manifest=Path.Combine(result.DirectoryPath,"manifest.csv");string text=File.ReadAllText(manifest);
                // Equivalent valid CSV quoting is allowed; publication still binds its exact bytes.
                File.WriteAllText(manifest,text.Replace(",test-session,",",\"test-session\","),new UTF8Encoding(false));
                PreviewExportReceipt.WriteCompleted(unicode,Session,Second,result);Check.Equal(result.DirectoryPath,Read(unicode,Second).DirectoryPath);Refused(unicode);
            }));
            t.Run("Final preview export verifies 104 trials and 20 ratings immediately after the last rating with three rests",()=>Temporary(root=>{
                var clock=new ManualClock();var allocation=new Allocation("completed-preview",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var experiment=new Experiment(new SessionConfiguration(Session,42,allocation,RunKind.Simulation,"en",3,30,true,3),clock);
                Fixtures.Practice(experiment,clock,8);
                for(int block=0;block<4;block++)
                {
                    experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");experiment.BeginCountdown(Actor.Owner,true);
                    clock.Advance(3);experiment.Tick();Check.Equal(Phase.MainTask,experiment.Phase);
                    for(int trial=0;trial<24;trial++)Fixtures.Answer(experiment,clock);
                    Check.Equal(Phase.Questionnaire,experiment.Phase);Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                    clock.Advance(3);experiment.Tick();
                    for(int item=0;item<4;item++)experiment.Rate(item+1);
                    Check.Equal(Phase.Questionnaire,experiment.Phase);experiment.Rate(5);
                    if(block<3){Check.Equal(Phase.Rest,experiment.Phase);clock.Advance(30);Check.True(experiment.Ready());}
                }
                // No extra Tick, rest or Ready action is needed after the last answer.
                Check.Equal(Phase.Ended,experiment.Phase);Check.Equal<EndReason?>(EndReason.Completed,experiment.EndReason);
                Check.Equal(104,experiment.Trials.Count);Check.Equal(20,experiment.Ratings.Count);
                Check.Equal(3,experiment.Events.Count(item=>item.Kind=="phase_changed"&&item.Fields["to"]=="Rest"));
                Check.Equal(3,experiment.Events.Count(item=>item.Kind=="rest_ready"));Check.Equal(1,experiment.Events.Count(item=>item.Kind=="final_rest_skipped"));
                var result=AuditExporter.Export(experiment,new AuditLog(Session,RunKind.Simulation),Path.Combine(root,"exports"),new ExportProvenance("completed-flow-fixture","synthetic presentation","synthetic input"));
                Check.True(result.Success,result.Error);Check.True(result.SessionCompleted);
                PreviewExportReceipt.WriteCompleted(root,Session,First,result);Check.Equal(result.DirectoryPath,Read(root).DirectoryPath);
                Check.Equal(105,File.ReadAllLines(Path.Combine(result.DirectoryPath,"trials.csv")).Length);
                Check.Equal(21,File.ReadAllLines(Path.Combine(result.DirectoryPath,"ratings.csv")).Length);
                foreach(string line in File.ReadAllLines(Path.Combine(result.DirectoryPath,"manifest.csv")).Skip(1))Check.True(line.Contains(",true,true,completed-flow-fixture"),"manifest did not preserve completed-session provenance");
            }));
        }
    }
}
