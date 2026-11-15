using System;
using System.Threading;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveNamedSessionFrameAdmission
    {
        long lastAcquisitionSequence = -1;
        double lastAcquisitionObserved = -1;

        // Archive the worker's FIFO independently of the current Unity/task
        // ticket. These records carry no historical head pose or trial claim.
        // The producer reservation and exact run's gate cover dequeue through
        // publication; a failure aborts rather than silently losing a sample.
        internal ViveProEyeWorker.StatusDisposition TryStageNextAcquisition()
        {
            if (Thread.CurrentThread.ManagedThreadId != owner || !taskChoice || !eyeChoice ||
                !statusEnabled || statusSealed || enabledAt < 0 || ioFence.IsRevoked ||
                (Volatile.Read(ref state) & (R | F | S)) != 0)
            { Fail(); return ViveProEyeWorker.StatusDisposition.Faulted; }
            if (!Monitor.TryEnter(worker.RecordingTicketGate)) return ViveProEyeWorker.StatusDisposition.Busy;
            try
            {
                var available = worker.ReadAcquisitionBacklog(runIdentity);
                if (available == ViveProEyeWorker.StatusDisposition.Empty || available == ViveProEyeWorker.StatusDisposition.Busy)
                    return available;
                if (available != ViveProEyeWorker.StatusDisposition.Event || !Reserve())
                { Fail(); return ViveProEyeWorker.StatusDisposition.Faulted; }
                ViveRawEyeAcquisition value;
                double now;
                if (worker.TryDequeueAcquisition(runIdentity, out value) != ViveProEyeWorker.StatusDisposition.Event ||
                    value == null || value.Poll == null || value.Poll.Eye == null ||
                    value.Sequence != lastAcquisitionSequence + 1 ||
                    !clock.TryReadRecordingAdmissionTime(out now) ||
                    value.Poll.Eye.ObservedLocalSeconds <= enabledAt ||
                    value.Poll.Eye.ObservedLocalSeconds < lastAcquisitionObserved ||
                    value.Poll.Eye.ObservedLocalSeconds > now || value.WorkerPublishedLocalSeconds > now ||
                    !Publish(Volatile.Read(ref ring), value))
                { Fail(); return ViveProEyeWorker.StatusDisposition.Faulted; }
                lastAcquisitionSequence = value.Sequence;
                lastAcquisitionObserved = value.Poll.Eye.ObservedLocalSeconds;
                return ViveProEyeWorker.StatusDisposition.Event;
            }
            catch (Exception error) when (!(error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException))
            { Fail(); return ViveProEyeWorker.StatusDisposition.Faulted; }
            finally { Monitor.Exit(worker.RecordingTicketGate); }
        }
    }
}
