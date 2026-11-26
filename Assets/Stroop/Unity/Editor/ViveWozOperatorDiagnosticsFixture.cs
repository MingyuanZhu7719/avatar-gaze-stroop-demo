#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // File-only fixture: no real session, network, Unity scene, XR or eye source.
    public static class ViveWozOperatorDiagnosticsFixture
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const string Offer = "WOZPROC4.U1lOVEhFVElDX1BBWVJFRF9TRUNSRVQ=";
        static int assertions;

        static void Require(bool value, string message)
        {
            if(!value) throw new InvalidOperationException("WoZ diagnostics fixture: " + message);
            assertions++;
        }
        static ViveWozOperatorDiagnostics Create(string[] roots, string playerLog)
        {
            return (ViveWozOperatorDiagnostics)Activator.CreateInstance(typeof(ViveWozOperatorDiagnostics),
                PrivateInstance, null, new object[] { roots, playerLog, false }, null);
        }
        static string Entry(string path, string name)
        {
            using(var file = File.OpenRead(path))
            using(var zip = new ZipArchive(file, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry(name);
                Require(entry != null, "missing ZIP entry " + name);
                using(var input = new StreamReader(entry.Open())) return input.ReadToEnd();
            }
        }
        static string ActiveText(string path)
        {
            using(var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using(var input = new StreamReader(file)) return input.ReadToEnd();
        }
        sealed class FullDiskStream : Stream
        {
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return 0; } }
            public override long Position { get { return 0; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new IOException("Synthetic disk full."); }
        }

        static void ErrorsAndStorage(string root)
        {
            string blocked = Path.Combine(root, "blocked-root");
            File.WriteAllText(blocked, "This file intentionally prevents directory creation.");
            using(var diagnostics = Create(new[] { blocked, Path.Combine(root, "fallback") }, null))
            {
                Require(diagnostics.DirectoryPath.StartsWith(Path.Combine(root, "fallback"), StringComparison.Ordinal), "writable fallback was not used");
                Require(diagnostics.StorageIssue.Length == 0, "successful fallback retained a failure");
                diagnostics.Record("STATE", "ready");
                string log = Path.Combine(diagnostics.DirectoryPath, "events.log");
                Require(ActiveText(log).Contains("ready"), "events were not flushed immediately");
                long firstSize = new FileInfo(log).Length;
                diagnostics.Record("STATE", "ready");
                Require(new FileInfo(log).Length == firstSize, "unchanged per-frame state was repeated");
                string[] timeline = { "HEAD_TRACKED", "HEAD_LOST", "HEAD_TRACKED", "HEAD_LOST", "HEAD_TRACKED" };
                foreach(string code in timeline)
                {
                    diagnostics.Record(code, "Synthetic tracking transition");
                    diagnostics.Record(code, "Synthetic tracking transition");
                }
                string[] recorded = Array.FindAll(ActiveText(log).Split('\n'), line => line.Contains("Synthetic tracking transition"));
                Require(recorded.Length == timeline.Length, "repeated tracking loss/recovery was omitted or consecutive duplicate retained");
                for(int i = 0; i < timeline.Length; i++)
                    Require(recorded[i].Contains("[" + timeline[i] + "]"), "tracking transitions were not preserved in order");
                diagnostics.Fail("XR_PRIMARY", "Retained primary failure", new IOException("Detail " + Offer));
                diagnostics.Record("XR_CLEANUP", "A later cleanup failure");
                var capture = typeof(ViveWozOperatorDiagnostics).GetMethod("CaptureUnityMessage", PrivateInstance);
                capture.Invoke(diagnostics, new object[] { "Later Unity failure", "Stack " + Offer, LogType.Exception });
                Require(diagnostics.LastErrorCode == "XR_PRIMARY" && diagnostics.LastError == "Retained primary failure", "secondary log replaced the primary failure");
                string export = Task.Run(() => diagnostics.Export("Synthetic summary " + Offer)).Result;
                Require(!String.IsNullOrEmpty(export), "background export failed with missing Player.log");
                Require(Entry(export, "Player.log").Contains("unavailable"), "missing Player.log did not have an explanation");
                Require(!Entry(export, "Summary.txt").Contains(Offer) && !Entry(export, "events.log").Contains(Offer), "pairing offer leaked into ZIP");
                diagnostics.ClearError();
                Require(diagnostics.LastError.Length == 0 && diagnostics.LastErrorCode.Length == 0, "explicit clear failed");
                capture.Invoke(diagnostics, new object[] { "First Unity failure", "stack", LogType.Error });
                Require(diagnostics.LastErrorCode == "UNITY_ERROR", "unhandled Unity error was not retained");

                // Force the next event write to fail without exhausting the real disk.
                var field = typeof(ViveWozOperatorDiagnostics).GetField("writer", PrivateInstance);
                ((StreamWriter)field.GetValue(diagnostics)).Dispose();
                field.SetValue(diagnostics, new StreamWriter(new FullDiskStream(), new UTF8Encoding(false)) { AutoFlush = true });
                diagnostics.Record("AFTER_DISK_FULL", "This event must remain in memory");
                Require(diagnostics.StorageIssue.Contains("failed"), "write failure was not exposed");
                Require(diagnostics.LastErrorCode == "UNITY_ERROR", "storage fault erased the causal XR error");
                string afterFailure = diagnostics.Export("Recovered memory events");
                Require(!String.IsNullOrEmpty(afterFailure) && Entry(afterFailure, "events.log").Contains("AFTER_DISK_FULL"), "write failure lost memory events or prevented a later export");
                Require(afterFailure != export, "a later export overwrote the first archive");
                diagnostics.Dispose();
                diagnostics.Record("DISPOSED", "ignored");
                Require(diagnostics.Export("closed") == null, "disposed diagnostics reported a successful export");
            }
            using(var unavailable = Create(new[] { blocked }, null))
            {
                unavailable.Fail("EARLY_FAILURE", "Failure without writable storage");
                Require(unavailable.DirectoryPath.Length == 0 && unavailable.StorageIssue.Length > 0, "unavailable storage was hidden");
                Require(unavailable.LastErrorCode == "EARLY_FAILURE" && unavailable.Export("unavailable") == null, "storage failure lost the visible error or reported a ZIP");
            }
        }

        static void ActiveLogAndLauncher(string root)
        {
            string outputRoot = Path.Combine(root, "WoZLogs");
            string run = Path.Combine(outputRoot, "run-synthetic-launcher");
            Directory.CreateDirectory(run);
            string playerLog = Path.Combine(run, "Player.log");
            File.WriteAllText(Path.Combine(run, "Launcher.txt"), "Synthetic launcher information");
            using(var player = new FileStream(playerLog, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            using(var output = new StreamWriter(player, new UTF8Encoding(false)))
            {
                string line = new string('x', 600);
                for(int i = 0; i < 8000; i++) output.WriteLine(line);
                output.WriteLine("LAST_PLAYER_LINE " + Offer);
                output.Flush();
                long originalLength = player.Length;
                using(var diagnostics = Create(new[] { outputRoot }, playerLog))
                {
                    Require(diagnostics.DirectoryPath == run, "launcher run directory was not reused");
                    diagnostics.Record("CONCURRENT", "initial");
                    Task logTask = Task.Run(() => { for(int i = 0; i < 1000; i++) diagnostics.Record("CONCURRENT", i.ToString()); });
                    string export = Task.Run(() => diagnostics.Export("Active-file snapshot")).Result;
                    logTask.Wait();
                    Require(!String.IsNullOrEmpty(export), "export failed while Player.log was open for writing");
                    string tail = Entry(export, "Player.log");
                    Require(tail.Contains("LAST_PLAYER_LINE") && tail.Contains("Earlier log content omitted"), "bounded tail missed the latest log line");
                    Require(tail.Length <= 4 * 1024 * 1024 + 100 && !tail.Contains(Offer), "Player.log size bound or redaction failed");
                    Require(Entry(export, "Launcher.txt").Contains("Synthetic launcher"), "launcher detail was not exported");
                    Require(player.Length == originalLength, "export changed the active Player.log");
                    using(var second = Create(new[] { outputRoot }, playerLog))
                        Require(second.DirectoryPath != run && second.DirectoryPath != diagnostics.DirectoryPath, "second diagnostics instance overwrote existing event log");
                }
            }
        }

        public static void Validate()
        {
            assertions = 0;
            string root = Path.Combine(Path.GetTempPath(), "AvatarStroop-WozDiagnosticsFixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                ErrorsAndStorage(root);
                ActiveLogAndLauncher(root);
                Debug.Log("WOZ_OPERATOR_DIAGNOSTICS_FIXTURE_PASS assertions=" + assertions);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
#endif
