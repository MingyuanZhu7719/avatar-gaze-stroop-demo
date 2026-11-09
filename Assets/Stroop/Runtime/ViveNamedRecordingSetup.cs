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
        internal string DirectoryPath { get; private set; }
        internal ViveRawByteBudget Budget { get; private set; }
        internal Step Current { get; private set; }
        internal int Revision { get; private set; }
        bool taskSelected,eyeSelected,armed,consumed;
        MonotonicClock selectionClock;
        double? taskPollSeconds,eyePollSeconds;
        internal bool IsTerminal { get { return Current==Step.Confirmed||Current==Step.Declined||Current==Step.Cancelled; } }
        internal bool BothSelected { get { return Current==Step.Confirmed&&taskSelected&&eyeSelected; } }
        internal bool CanStart(bool gate,bool focused,bool tracking,bool workerReady,int freshFrame,int currentFrame)
        {return gate&&BothSelected&&focused&&tracking&&workerReady&&freshFrame>=0&&freshFrame==currentFrame;}

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
            if(Current==Step.Confirmed&&(!ready||changed||clock==null||!Object.ReferenceEquals(clock,selectionClock))){Cancel();return;}
            if(IsTerminal)return;
            if(!ready||changed){Reset();return;}
            // The view clock also becomes the configured task clock. A new
            // view or unavailable clock cannot inherit an earlier decision.
            if(clock==null||(selectionClock!=null&&!Object.ReferenceEquals(clock,selectionClock))){Reset();return;}
            if(!visible){armed=false;return;}
            if(!armed){if(!trigger&&!menu&&!trackpad)armed=true;return;}
            if(menu){ClearTimes();taskSelected=false;eyeSelected=false;DirectoryPath=null;Budget=null;Current=Step.Declined;armed=false;Revision++;return;}
            if(!trigger)return;
            double observed;
            if(!clock.TryReadRecordingAdmissionTime(out observed)||
                (taskPollSeconds.HasValue&&observed<taskPollSeconds.Value)){Cancel();return;}
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
        {if(!taskSelected&&!eyeSelected&&!armed&&Current==Step.Task)return;ClearTimes();taskSelected=false;eyeSelected=false;armed=false;Current=Step.Task;Revision++;}
        internal void Cancel()
        {if(Current==Step.Cancelled)return;ClearTimes();taskSelected=false;eyeSelected=false;armed=false;DirectoryPath=null;Budget=null;Current=Step.Cancelled;Revision++;}
    }
}
