namespace AvatarStroop.Runtime
{
    // Owner-facing status only. It cannot start, stop, authorize or export a
    // recording, and it never treats software completion as data validity.
    internal static class ViveRawRecordingPresentation
    {
        internal static string Describe(bool installFailed,bool withdrawn,bool safetyLatched,ViveNamedRawRecordingPump.Outcome state)
        {
            if(withdrawn)return "Withdrawal requested. Recording is stopped or stopping; unconfirmed data remains unknown.";
            if(installFailed)return "Recording could not start. The task is blocked; cleanup and any in-flight work remain unconfirmed.";
            if(safetyLatched)return "A safety stop is active. Recording cleanup and the unconfirmed tail remain unknown.";
            switch(state)
            {
                case ViveNamedRawRecordingPump.Outcome.Running:
                    return "Development recording is running. Coverage, validity and export are not confirmed.";
                case ViveNamedRawRecordingPump.Outcome.Finishing:
                    return "Recording is finishing. The unconfirmed tail remains unknown.";
                case ViveNamedRawRecordingPump.Outcome.CloseCallsReturned:
                    return "Recording close calls returned. Complete coverage, export and durable storage are not confirmed.";
                case ViveNamedRawRecordingPump.Outcome.StopPending:
                    return "Recording is stopping. Cleanup and the unconfirmed tail remain unknown.";
                case ViveNamedRawRecordingPump.Outcome.StoppedWithoutClose:
                    return "Recording stopped without confirmed close. The unconfirmed tail remains unknown.";
                case ViveNamedRawRecordingPump.Outcome.WithdrawnWithoutClose:
                    return "Recording was withdrawn without confirmed close. The unconfirmed tail remains unknown.";
                case ViveNamedRawRecordingPump.Outcome.Faulted:
                    return "Recording faulted. The task is blocked and the unconfirmed tail remains unknown.";
                default:
                    return "Recording I/O is unconfirmed. The task is blocked and the unconfirmed tail remains unknown.";
            }
        }
    }
}
