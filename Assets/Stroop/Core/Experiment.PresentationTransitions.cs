using System;

namespace AvatarStroop.Core
{
    public sealed partial class Experiment
    {
        // The immutable event tail is already captured by both observation
        // journals. No second mutable animation state can escape their checks.
        JournalEvent CurrentBlockCelebrationEvent()
        {
            for(int i=events.Count-1;i>=0;i--)
            {
                var item=events[i];if(item.BlockIndex!=BlockIndex)break;
                if(item.Kind=="block_celebration_started"||item.Kind=="block_celebration_completed")return item;
            }
            return null;
        }
        bool BlockCelebrationPending
        {get{var item=CurrentBlockCelebrationEvent();return item!=null&&item.Kind=="block_celebration_started";}}
        internal void BeginBlockCelebration()
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){BeginBlockCelebrationCore();return;}
            BeginMutation("BeginBlockCelebration");bool success=false;
            try{BeginBlockCelebrationCore();success=true;}finally{EndMutation(success);}
        }
        void BeginBlockCelebrationCore()
        {
            Require(Phase.Questionnaire);
            if(questionIndex!=0)throw new InvalidOperationException("Block celebration must precede questionnaire responses");
            if(CurrentBlockCelebrationEvent()!=null)return;
            Log("block_celebration_started","required_rendered_seconds","6","presentation","gentle_fireworks",
                "questionnaire_input","held_until_celebration_completed");
        }
        internal void EndBlockCelebration()
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){EndBlockCelebrationCore();return;}
            BeginMutation("EndBlockCelebration");bool success=false;
            try{EndBlockCelebrationCore();success=true;}finally{EndMutation(success);}
        }
        void EndBlockCelebrationCore()
        {
            Require(Phase.Questionnaire);
            var started=CurrentBlockCelebrationEvent();
            if(started!=null&&started.Kind=="block_celebration_completed")return;
            if(started==null||questionIndex!=0)throw new InvalidOperationException("No current block celebration");
            double now=Time();
            Log("block_celebration_completed","actual_elapsed_seconds",Number(now-started.Time),
                "required_rendered_seconds","6","duration_policy","excluded_from_questionnaire_duration");
            // Start the questionnaire clock after the animation, including
            // any tracking/focus hold. Do not subtract an assumed six seconds.
            ratingAt=now;TraceMutation();
        }
    }
}
