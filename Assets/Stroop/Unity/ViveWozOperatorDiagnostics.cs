#if STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Local troubleshooting only. Recording errors must never propagate into
    // the XR or source-stop paths. All Unity values are captured on the owner thread.
    public sealed class ViveWozOperatorDiagnostics : IDisposable
    {
        const int MaximumText = 8192;
        const int MaximumLogBytes = 4 * 1024 * 1024;
        const int MaximumRecentEntries = 256;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly Regex PairingOffer = new Regex(@"WOZPROC[0-9]+\.[A-Za-z0-9+/=]+", RegexOptions.CultureInvariant);
        readonly object gate = new object();
        readonly Queue<string> recent = new Queue<string>();
        readonly string playerLog;
        readonly bool subscribed;
        StreamWriter writer;
        long eventBytes;
        bool disposed;
        string directoryPath = "";
        string lastError = "";
        string lastErrorCode = "";
        string storageIssue = "";
        string previousCode;
        string previousMessage;

        public string DirectoryPath { get { lock(gate) return directoryPath; } }
        public string LastError { get { lock(gate) return lastError; } }
        public string LastErrorCode { get { lock(gate) return lastErrorCode; } }
        public string StorageIssue { get { lock(gate) return storageIssue; } }

        public ViveWozOperatorDiagnostics()
            : this(new[] {
                Path.Combine(Path.GetDirectoryName(Application.dataPath), "WoZLogs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvatarStroop", "WoZLogs")
            }, Application.consoleLogPath, true)
        {
            Record("APP_START", "Unity " + Application.unityVersion + " | Application " + Application.version);
        }

        // Separate paths allow the Editor fixture to exercise unavailable
        // storage and actively written logs without changing a real session.
        internal ViveWozOperatorDiagnostics(string outputRoot, string playerLogPath)
            : this(new[] { outputRoot }, playerLogPath, false) { }

        internal ViveWozOperatorDiagnostics(string[] candidateRoots, string logPath, bool receiveUnityMessages)
        {
            playerLog = logPath;
            string run = "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0,8);
            foreach(string root in candidateRoots)
            {
                try
                {
                    string folder = ExistingRunFolder(root, logPath) ?? Path.Combine(root, run);
                    Directory.CreateDirectory(folder);
                    writer = OpenWriter(Path.Combine(folder, "events.log"));
                    directoryPath = folder;
                    storageIssue = "";
                    break;
                }
                catch(Exception error) when(IsStorageError(error))
                { storageIssue = "Cannot create diagnostic files (" + error.GetType().Name + ")."; }
            }
            subscribed = receiveUnityMessages;
            if(subscribed) Application.logMessageReceivedThreaded += CaptureUnityMessage;
        }

        static bool IsStorageError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException ||
                error is System.Security.SecurityException || error is ArgumentException || error is NotSupportedException;
        }
        static string ExistingRunFolder(string root, string logPath)
        {
            if(String.IsNullOrEmpty(logPath)) return null;
            string folder = Path.GetDirectoryName(Path.GetFullPath(logPath));
            string parent = Path.GetDirectoryName(folder);
            if(!String.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(folder).StartsWith("run-", StringComparison.Ordinal) ||
                File.Exists(Path.Combine(folder, "events.log"))) return null;
            return folder;
        }
        static StreamWriter OpenWriter(string path)
        {
            return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Utf8) { AutoFlush = true };
        }
        static string Clean(string value, int maximum)
        {
            if(String.IsNullOrEmpty(value)) return "";
            if(value.Length > maximum) value = value.Substring(0, maximum) + " [truncated]";
            return PairingOffer.Replace(value, "[pairing offer removed]").Replace('\0', ' ');
        }

        public void Record(string code, string message)
        {
            lock(gate)
            {
                if(disposed) return;
                Append(Clean(code, 80), Clean(message, MaximumText));
            }
        }
        public void Fail(string code, string message, Exception error = null)
        {
            lock(gate)
            {
                if(disposed) return;
                lastErrorCode = Clean(code, 80);
                lastError = Clean(message, MaximumText);
                string detail = lastError;
                if(error != null) detail += "\n" + Clean(error.ToString(), MaximumText);
                Append(lastErrorCode, detail);
            }
        }
        public void ClearError()
        {
            lock(gate)
            {
                lastError = "";
                lastErrorCode = "";
            }
        }
        void CaptureUnityMessage(string message, string stack, LogType type)
        {
            if(type == LogType.Log) return;
            lock(gate)
            {
                if(disposed) return;
                string code = "UNITY_" + type.ToString().ToUpperInvariant();
                string text = Clean(message, MaximumText);
                if(type != LogType.Warning && String.IsNullOrEmpty(lastError))
                {
                    lastErrorCode = code;
                    lastError = text;
                }
                Append(code, text + "\n" + Clean(stack, MaximumText));
            }
        }
        void Append(string code, string message)
        {
            // Suppress only consecutive duplicates. A repeated failure or
            // recovery after another event is a new point in the timeline.
            if(previousCode == code && previousMessage == message) return;
            previousCode = code;
            previousMessage = message;
            string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " [" + code + "] " + message;
            recent.Enqueue(line);
            while(recent.Count > MaximumRecentEntries) recent.Dequeue();
            if(writer == null) return;
            try
            {
                int bytes = Utf8.GetByteCount(line) + 2;
                if(eventBytes + bytes > MaximumLogBytes)
                {
                    writer.Dispose();
                    writer = null;
                    writer = OpenWriter(Path.Combine(directoryPath, "events.log"));
                    eventBytes = 0;
                }
                writer.WriteLine(line);
                eventBytes += bytes;
            }
            catch(Exception error) when(IsStorageError(error))
            {
                storageIssue = "Diagnostic file write failed (" + error.GetType().Name + "). Recent events remain available in memory.";
                CloseWriter();
            }
        }
        void CloseWriter()
        {
            StreamWriter closing = writer;
            writer = null;
            if(closing == null) return;
            try { closing.Dispose(); }
            catch(Exception error) when(IsStorageError(error))
            { storageIssue = "Diagnostic file close failed (" + error.GetType().Name + ")."; }
        }

        // FileShare permits a snapshot while Unity is still writing. Bound the
        // read to the length observed at open, and drop a partial first line.
        static string ReadTail(string path)
        {
            using(var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = input.Length;
                long start = Math.Max(0, length - MaximumLogBytes);
                int count = (int)(length - start);
                input.Position = start;
                var buffer = new byte[count];
                int read = 0;
                while(read < count)
                {
                    int next = input.Read(buffer, read, count - read);
                    if(next == 0) break;
                    read += next;
                }
                string text = Utf8.GetString(buffer, 0, read);
                if(start > 0)
                {
                    int newline = text.IndexOf('\n');
                    text = newline >= 0 ? text.Substring(newline + 1) : "[Last log line exceeded the diagnostic size limit.]";
                    text = "[Earlier log content omitted.]\n" + text;
                }
                return Clean(text, MaximumLogBytes);
            }
        }
        static void WriteEntry(ZipArchive zip, string name, string text)
        {
            using(var output = new StreamWriter(zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest).Open(), Utf8))
                output.Write(text);
        }
        public string Export(string summary)
        {
            string folder, eventSnapshot, issue, errorCode, errorMessage;
            lock(gate)
            {
                if(disposed || String.IsNullOrEmpty(directoryPath))
                {
                    storageIssue = "Diagnostic storage is unavailable. Copy the displayed error or take a screenshot.";
                    return null;
                }
                folder = directoryPath;
                eventSnapshot = String.Join("\n", recent.ToArray());
                issue = storageIssue;
                errorCode = lastErrorCode;
                errorMessage = lastError;
            }
            string outputPath = Path.Combine(folder, "WoZ-debug-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0,8) + ".zip");
            try
            {
                string playerText;
                try { playerText = ReadTail(playerLog); }
                catch(Exception error) when(IsStorageError(error))
                { playerText = "Player.log was unavailable (" + error.GetType().Name + "). The application events are included in events.log."; }
                using(var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using(var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, "Summary.txt", Clean(summary, 65536) + "\n\nLast error code: " + errorCode +
                        "\nLast error: " + errorMessage + "\nDiagnostic storage: " + issue +
                        "\nExported UTC: " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    WriteEntry(zip, "events.log", eventSnapshot);
                    WriteEntry(zip, "Player.log", playerText);
                    string launcherPath = Path.Combine(folder, "Launcher.txt");
                    if(File.Exists(launcherPath))
                    {
                        string launcherText;
                        try { launcherText = ReadTail(launcherPath); }
                        catch(Exception error) when(IsStorageError(error))
                        { launcherText = "Launcher.txt was unavailable (" + error.GetType().Name + ")."; }
                        WriteEntry(zip, "Launcher.txt", launcherText);
                    }
                }
                Record("DIAGNOSTIC_EXPORT", "Created " + Path.GetFileName(outputPath));
                return outputPath;
            }
            catch(Exception error) when(IsStorageError(error))
            {
                lock(gate) storageIssue = "Could not export the diagnostic ZIP (" + error.GetType().Name + "). Copy the displayed error or take a screenshot.";
                // Retain any partial file for diagnosis and never report it as a complete export.
                return null;
            }
        }
        public void Dispose()
        {
            if(subscribed) Application.logMessageReceivedThreaded -= CaptureUnityMessage;
            lock(gate)
            {
                if(disposed) return;
                disposed = true;
                CloseWriter();
            }
        }
    }
}
#endif
