using System;
using System.IO;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Software selections only. They do not authenticate a wearer, establish
    // informed consent, reserve space, create files or authorize publication.
    internal sealed class ViveNamedRecordingSetup
    {
        internal enum Step { Task, Eye, Confirmed, Declined, Cancelled }
        internal enum CancellationCause
        { None, ExplicitCancellation, TrackingOrInputUnavailable, ControllerIdentityChanged, ClockChanged, TimingUnavailable, DesignChanged, ViewStopped, ComponentDisabled }
        internal string DirectoryPath { get; private set; }
        internal ViveRawByteBudget Budget { get; private set; }
        internal Step Current { get; private set; }
        internal int Revision { get; private set; }
        internal CancellationCause Cancellation { get; private set; }
        internal string CancellationMessage
        {
            get
            {
                switch(Cancellation)
                {
                    case CancellationCause.TrackingOrInputUnavailable:return "Headset or controller tracking became unavailable.";
                    case CancellationCause.ControllerIdentityChanged:return "The controller identity changed.";
                    case CancellationCause.ClockChanged:return "The headset session clock changed or became unavailable.";
                    case CancellationCause.TimingUnavailable:return "A valid recording-choice observation time was unavailable.";
                    case CancellationCause.DesignChanged:return "The recording design or local recording selection changed.";
                    case CancellationCause.ViewStopped:return "The headset view was stopped.";
                    case CancellationCause.ComponentDisabled:return "The recording interface was closed or disabled.";
                    case CancellationCause.ExplicitCancellation:return "Recording choices were cancelled explicitly.";
                    default:return "No recording-choice cancellation has been reported.";
                }
            }
        }
        bool taskSelected,eyeSelected,armed,consumed;
        internal bool TrackingSuspended {get;private set;}
        MonotonicClock selectionClock;
        double? taskPollSeconds,eyePollSeconds;
        internal bool IsTerminal { get { return Current==Step.Confirmed||Current==Step.Declined||Current==Step.Cancelled; } }
        internal bool BothSelected { get { return Current==Step.Confirmed&&taskSelected&&eyeSelected; } }
        internal bool CanStart(bool gate,bool focused,bool tracking,bool workerReady,int freshFrame,int currentFrame)
        {return gate&&BothSelected&&!TrackingSuspended&&focused&&tracking&&workerReady&&freshFrame>=0&&freshFrame==currentFrame;}
        // Focus changes preserve existing software choices and storage, but
        // a held trigger must not become the next choice on restoration.
        internal void SuspendInputForFocus(){armed=false;}
        // Temporary tracking unavailability is not withdrawal of either choice.
        // Keep the same clock-bound intent and folder, but never retain start readiness.
        internal void SuspendInputForTracking(){armed=false;TrackingSuspended=true;}

        ViveNamedRecordingSetup(string directory,ViveRawByteBudget budget)
        { DirectoryPath=directory;Budget=budget;Current=Step.Task; }

        // The availability argument is only supplied by the scene's actual
        // gate. Injecting it and the existence check permits file-free tests.
        internal static ViveNamedRecordingSetup TryPrepare(bool available,string directory,string budgetMiB,
            Func<string,bool> directoryExists,out string reason)
        {
            reason=null;
            if(!available){reason="Recording is unavailable; no selection or file was created.";return null;}
            try
            {
                if(String.IsNullOrWhiteSpace(directory)||directory.Length<3||!Char.IsLetter(directory[0])||directory[1]!=':'||
                    (directory[2]!='\\'&&directory[2]!='/'))
                    throw new ArgumentException("Enter an absolute local Windows folder path.");
                string full=Path.GetFullPath(directory);
                if(directoryExists==null||!directoryExists(full))throw new ArgumentException("The selected folder does not exist or cannot be accessed.");
                long mib;
                if(!Int64.TryParse(budgetMiB,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out mib)||mib<=0)
                    throw new ArgumentException("Enter a positive storage limit in MiB.");
                long bytes=checked(mib*1048576L);
                return new ViveNamedRecordingSetup(full,ViveRawByteBudget.SelectExplicit(bytes));
            }
            catch(Exception error) when(error is ArgumentException||error is IOException||error is UnauthorizedAccessException||error is System.Security.SecurityException||error is OverflowException||error is NotSupportedException)
            {reason=error.Message;return null;}
        }

        // Called only from the headset controller polling path. Each prompt
        // requires its own visible frame and a fresh all-controls release.
        internal void Observe(bool ready,bool visible,bool trigger,bool menu,bool trackpad,bool changed,MonotonicClock clock)
        {
            if(Current==Step.Declined||Current==Step.Cancelled)return;
            if(changed)
            {if(Current==Step.Confirmed)Cancel(CancellationCause.ControllerIdentityChanged);else Reset();return;}
            if(!ready){SuspendInputForTracking();return;}
            if(Current==Step.Confirmed)
            {
                if(clock==null||!Object.ReferenceEquals(clock,selectionClock)){Cancel(CancellationCause.ClockChanged);return;}
                if(!trigger&&!menu&&!trackpad)TrackingSuspended=false;
                return;
            }
            // A different clock still cannot inherit a previous decision.
            if(clock==null||(selectionClock!=null&&!Object.ReferenceEquals(clock,selectionClock))){Reset();return;}
            TrackingSuspended=false;
            if(!visible){armed=false;return;}
            if(!armed){if(!trigger&&!menu&&!trackpad)armed=true;return;}
            if(menu){ClearTimes();taskSelected=false;eyeSelected=false;DirectoryPath=null;Budget=null;Current=Step.Declined;armed=false;Revision++;return;}
            if(!trigger)return;
            double observed;
            if(!clock.TryReadRecordingAdmissionTime(out observed)||
                (taskPollSeconds.HasValue&&observed<taskPollSeconds.Value)){Cancel(CancellationCause.TimingUnavailable);return;}
            armed=false;
            if(Current==Step.Task){selectionClock=clock;taskPollSeconds=observed;taskSelected=true;Current=Step.Eye;}
            else if(Current==Step.Eye){eyePollSeconds=observed;eyeSelected=true;Current=Step.Confirmed;}
            Revision++;
        }
        // These are times when this process observed controller input, not
        // hardware event times, proof of identity or informed consent.
        internal bool TryGetObservedTimes(MonotonicClock clock,out double task,out double eye)
        {
            task=eye=0;
            if(!BothSelected||clock==null||!Object.ReferenceEquals(clock,selectionClock)||
                !taskPollSeconds.HasValue||!eyePollSeconds.HasValue||eyePollSeconds.Value<taskPollSeconds.Value)return false;
            task=taskPollSeconds.Value;eye=eyePollSeconds.Value;return true;
        }
        internal bool TryConsumeObservedTimes(MonotonicClock clock,out double task,out double eye)
        {
            task=eye=0;if(consumed||!TryGetObservedTimes(clock,out task,out eye))return false;
            consumed=true;return true;
        }
        void ClearTimes(){selectionClock=null;taskPollSeconds=null;eyePollSeconds=null;}
        void Reset()
        {TrackingSuspended=false;if(!taskSelected&&!eyeSelected&&!armed&&Current==Step.Task)return;ClearTimes();taskSelected=false;eyeSelected=false;armed=false;Current=Step.Task;Revision++;}
        internal void Cancel(){Cancel(CancellationCause.ExplicitCancellation);}
        internal void Cancel(CancellationCause cause)
        {
            if(!Enum.IsDefined(typeof(CancellationCause),cause)||cause==CancellationCause.None)
                throw new ArgumentOutOfRangeException("cause");
            if(Current==Step.Cancelled)return;
            Cancellation=cause;ClearTimes();taskSelected=false;eyeSelected=false;armed=false;TrackingSuspended=false;
            DirectoryPath=null;Budget=null;Current=Step.Cancelled;Revision++;
        }
    }
}
