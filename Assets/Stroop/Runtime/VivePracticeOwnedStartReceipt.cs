using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Internal carrier for the sealed fake target only. A published receipt
    // denotes one structurally accepted in-memory append at a local completion
    // point; it is not consent identity, hardware, real file, or durability.
    internal sealed class VivePracticeOwnedStartReceipt
    {
        internal readonly VivePracticeOwnedMemoryStart Issuer;
        internal readonly ViveDevelopmentParticipant Participant;
        internal readonly VivePracticeConsentAuthority Choices;
        internal readonly IClock Clock;
        internal readonly ViveExclusiveMemoryTarget.Receipt TargetReceipt;
        internal readonly VivePracticeStartBatch Batch;
        internal readonly long ChoiceGeneration;
        internal VivePracticeOwnedStartReceipt(VivePracticeOwnedMemoryStart issuer,ViveDevelopmentParticipant participant,
            VivePracticeConsentAuthority choices,IClock clock,ViveExclusiveMemoryTarget.Receipt targetReceipt,
            VivePracticeStartBatch batch,long choiceGeneration)
        {Issuer=issuer;Participant=participant;Choices=choices;Clock=clock;TargetReceipt=targetReceipt;Batch=batch;ChoiceGeneration=choiceGeneration;}
    }
}
