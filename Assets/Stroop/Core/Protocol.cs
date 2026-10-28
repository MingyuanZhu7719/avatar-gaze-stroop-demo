using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AvatarStroop.Core
{
    public enum Colour { Red, Green, Blue, Yellow }
    public enum Condition { A, B, C, D }
    public enum GazeTarget { Participant, Task }
    public enum Actor { Participant, Operator, Owner }
    public enum ControlSelection { Automatic, LiveWoz, SimulatorRehearsal }
    public enum RunKind { Simulation, Apparatus }
    public enum Phase { Preparation, Familiarization, Practice, PracticeCorrection, PracticeRetry,
        BlockPreparation, BlockReady, Countdown, MainTask, Questionnaire, Rest, Paused,
        NeedsProtocolDecision, Ended }
    public enum EndReason { Completed, PracticeFailed, Withdrawn, TechnicalEnd }
    public enum TrialOutcome { Pending, Responded, Interrupted }

    public sealed class Stimulus
    {
        public Colour Word { get; private set; }
        public Colour Ink { get; private set; }
        public bool Congruent { get { return Word == Ink; } }
        public string Key { get { return Word + "/" + Ink; } }
        public Stimulus(Colour word, Colour ink)
        {
            if (!Enum.IsDefined(typeof(Colour), word) || !Enum.IsDefined(typeof(Colour), ink))
                throw new ArgumentOutOfRangeException("colour");
            Word = word; Ink = ink;
        }
    }

    public sealed class Allocation
    {
        readonly Condition[] order;
        readonly int[] lists;
        public string AllocationId { get; private set; }
        public Condition[] Order { get { return (Condition[])order.Clone(); } }
        public GazeTarget BInitialTarget { get; private set; }
        public int[] ListByBlock { get { return (int[])lists.Clone(); } }
        public Allocation(string allocationId, Condition[] order, GazeTarget bInitialTarget, int[] listByBlock)
        {
            if (String.IsNullOrWhiteSpace(allocationId) || order == null || listByBlock == null ||
                order.Length != 4 || listByBlock.Length != 4) throw new ArgumentException("Invalid allocation");
            var names = String.Join("", Array.ConvertAll(order, x => x.ToString()));
            if (names != "ABDC" && names != "BCAD" && names != "CDBA" && names != "DACB")
                throw new ArgumentException("Use an assigned Williams order");
            var seen = new HashSet<int>(listByBlock);
            if (seen.Count != 4 || !seen.SetEquals(new[] {0,1,2,3}) ||
                !Enum.IsDefined(typeof(GazeTarget), bInitialTarget)) throw new ArgumentException("Invalid factors");
            AllocationId = allocationId; this.order = (Condition[])order.Clone();
            BInitialTarget = bInitialTarget; lists = (int[])listByBlock.Clone();
        }
    }

    public interface IClock { double Now { get; } string ClockId { get; } }

    public sealed class SessionConfiguration
    {
        public string SessionId { get; private set; }
        public uint Seed { get; private set; }
        public Allocation Allocation { get; private set; }
        public RunKind RunKind { get; private set; }
        public string QuestionnaireLanguage { get; private set; }
        public double CountdownSeconds { get; private set; }
        public const string ProtocolVersion = "2026-10-28-prd-66dbdb5";
        public const string SoftwareVersion = "0.4.0-dev";
        // No countdown or questionnaire language is silently selected for a study.
        public SessionConfiguration(string sessionId, uint seed, Allocation allocation, RunKind runKind,
            string questionnaireLanguage, double countdownSeconds)
        {
            if (String.IsNullOrWhiteSpace(sessionId) || allocation == null) throw new ArgumentException("Session identity required");
            if (questionnaireLanguage != "en" && questionnaireLanguage != "zh") throw new ArgumentException("Choose versioned en or zh wording");
            if (Double.IsNaN(countdownSeconds) || Double.IsInfinity(countdownSeconds) || countdownSeconds <= 0)
                throw new ArgumentOutOfRangeException("countdownSeconds", "Explicit provisional countdown must be positive");
            if (!Enum.IsDefined(typeof(RunKind), runKind)) throw new ArgumentException("Invalid run kind");
            SessionId = sessionId; Seed = seed; Allocation = allocation; RunKind = runKind;
            QuestionnaireLanguage = questionnaireLanguage; CountdownSeconds = countdownSeconds;
        }
    }

    public sealed class TrialRecord
    {
        public string TrialId { get; internal set; }
        public string SessionId { get; internal set; }
        public string ListId { get; internal set; }
        public bool IsPractice { get; internal set; }
        public int PracticeAttempt { get; internal set; }
        public int BlockIndex { get; internal set; }
        public int PlannedIndex { get; internal set; }
        public int Segment { get; internal set; }
        public Condition? Condition { get; internal set; }
        public Stimulus Stimulus { get; internal set; }
        public double? Onset { get; internal set; }
        public double? ResponseAt { get; internal set; }
        public Colour? Response { get; internal set; }
        public bool? Correct { get; internal set; }
        public double? ReactionSeconds { get { return Outcome == TrialOutcome.Responded && Onset.HasValue ? ResponseAt - Onset : null; } }
        public TrialOutcome Outcome { get; internal set; }
        public bool TechnicalIssue { get; internal set; }
    }

    public sealed class JournalEvent
    {
        public long Sequence { get; private set; }
        public double Time { get; private set; }
        public string ClockId { get; private set; }
        public string SessionId { get; private set; }
        public int BlockIndex { get; private set; }
        public int Segment { get; private set; }
        public string TrialId { get; private set; }
        public string Kind { get; private set; }
        public IReadOnlyDictionary<string, string> Fields { get; private set; }
        public JournalEvent(long sequence, double time, string clockId, string sessionId, int blockIndex,
            int segment, string trialId, string kind, IDictionary<string,string> fields)
        {
            Sequence=sequence; Time=time; ClockId=clockId; SessionId=sessionId; BlockIndex=blockIndex;
            Segment=segment; TrialId=trialId; Kind=kind;
            Fields=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>(fields));
        }
    }

    public sealed class RatingRecord
    {
        public int BlockIndex { get; internal set; }
        public string ItemId { get; internal set; }
        public int Value { get; internal set; }
        public double Time { get; internal set; }
        public string Language { get; internal set; }
        public string Version { get; internal set; }
    }

    public sealed class TaskView
    {
        public Phase Phase { get; internal set; }
        public int BlockIndex {get;internal set;}
        public int Segment {get;internal set;}
        public string TrialId { get; internal set; }
        public Stimulus Stimulus { get; internal set; }
        public bool OperatorAvatarVisible { get; internal set; }
        public bool ParticipantAvatarVisible { get; internal set; }
        public bool OperatorViewProvisional { get; internal set; }
        public string PracticeCorrection { get; internal set; }
        public string QuestionnaireItem { get; internal set; }
        public bool ReadyEnabled { get; internal set; }
        public string SourceLabel { get; internal set; }
    }
}
