#if STROOP_XR_PREVIEW
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.OpenXR;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveWozOperatorDevelopmentBootstrap
    {
        string DiagnosticSummary()
        {
            var report=new StringBuilder();
            report.AppendLine("WoZ Operator diagnostic snapshot");
            report.AppendLine("UTC: "+DateTime.UtcNow.ToString("o"));
            report.AppendLine("Version: "+Application.version+" | Build: "+Application.buildGUID);
            report.AppendLine("Unity: "+Application.unityVersion);
            report.AppendLine("OS: "+SystemInfo.operatingSystem);
            report.AppendLine("Graphics: "+SystemInfo.graphicsDeviceName+" | "+SystemInfo.graphicsDeviceVersion);
            report.AppendLine("OpenXR runtime: "+OpenXRRuntime.name);
            report.AppendLine("Requested runtime: "+Environment.GetEnvironmentVariable("XR_RUNTIME_JSON"));
            report.AppendLine("Desktop focus: "+Application.isFocused);
            report.AppendLine("Status: "+status+" | Network open: "+NetworkOpen);
            if(easyPairing!=null)report.AppendLine("Photon connection: "+easyPairing.Status+" | "+easyPairing.FailureReason);
            if(!String.IsNullOrEmpty(easyPairingTiming))
            {
                report.AppendLine(easyPairing!=null?"Current cloud timing:":"Last cloud timing snapshot:");
                report.AppendLine(easyPairingTiming);
            }
            report.AppendLine("Local endpoint: "+localIp+":"+localPort);
            report.AppendLine("Participant endpoint: "+remoteIp+":"+remotePort+" | Timeout: "+maximumAge);
            if(rig!=null)
            {
                report.AppendLine("Headset: "+rig.HeadTrackingStatus);
                report.AppendLine("Eye source: "+rig.EyeStatus);
                report.AppendLine("Eye client installation: "+rig.EyeInstallationDiagnostic);
                report.AppendLine("Rig: "+rig.Status);
                if(rig.Worker!=null)report.AppendLine("Worker: "+rig.Worker.Diagnostic);
            }
            if(process!=null)report.AppendLine("Connection issue: "+process.LastIssue);
            if(diagnostics!=null)report.AppendLine("Last error: "+diagnostics.LastErrorCode+" | "+diagnostics.LastError);
            return report.ToString();
        }
        void DrawDiagnosticActions()
        {
            GUILayout.BeginHorizontal();
            if(OwnerButton("Export debug ZIP",diagnostics!=null&&diagnosticExport==null))
            {
                try
                {
                    // Capture Unity state on the main thread, then copy logs in
                    // the background so an export cannot stall live forwarding.
                    string summary=DiagnosticSummary();var service=diagnostics;
                    diagnosticExport=Task.Run(()=>service.Export(summary));exportStatus="Exporting diagnostics...";
                }
                catch(Exception e){diagnostics.Fail("DEBUG_EXPORT_FAILED","Could not start the export. Copy error details instead.",e);}
            }
            if(OwnerButton("Copy error details",diagnostics!=null))
                try{GUIUtility.systemCopyBuffer=DiagnosticSummary();exportStatus="Error details copied.";}
                catch(Exception e){diagnostics.Fail("DEBUG_COPY_FAILED","Could not copy details. Use Export debug ZIP.",e);}
            GUILayout.EndHorizontal();
            if(!String.IsNullOrEmpty(exportStatus))GUILayout.Label(exportStatus,mutedStyle);
            GUILayout.BeginHorizontal();
            if(OwnerButton("Open logs folder",diagnostics!=null&&!String.IsNullOrEmpty(diagnostics.DirectoryPath)&&!NetworkOpen))
                try{Process.Start(new ProcessStartInfo(diagnostics.DirectoryPath){UseShellExecute=true});}
                catch(Exception e){diagnostics.Fail("LOG_FOLDER_OPEN_FAILED","Could not open Explorer. The folder path is under Show diagnostics.",e);}
            if(OwnerButton("Dismiss displayed error",diagnostics!=null&&!String.IsNullOrEmpty(diagnostics.LastError)))diagnostics.ClearError();
            GUILayout.EndHorizontal();
            if(NetworkOpen)GUILayout.Label("Stop forwarding before opening Explorer. Debug ZIP export stays available.",mutedStyle);
        }
        void PollDiagnosticExport()
        {
            if(diagnosticExport==null||!diagnosticExport.IsCompleted)return;
            try
            {
                string path=diagnosticExport.GetAwaiter().GetResult();
                exportStatus=String.IsNullOrEmpty(path)?"Export failed: "+diagnostics.StorageIssue:"Saved in logs folder: "+System.IO.Path.GetFileName(path);
            }
            catch(Exception e){diagnostics.Fail("DEBUG_EXPORT_FAILED","Export failed. Copy error details instead.",e);exportStatus="Export failed.";}
            finally{diagnosticExport=null;}
        }
    }
}
#endif
