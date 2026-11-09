#if STROOP_XR_PREVIEW
using System;
using System.Runtime.InteropServices;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace AvatarStroop.Unity
{
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(
        UiName = "Vive Verified Presence",
        BuildTargetGroups = new[] { UnityEditor.BuildTargetGroup.Standalone },
        Company = "AvatarStroop",
        Desc = "Optionally verifies physical user-presence capability for view alignment.",
        Version = "1.0.0",
        FeatureId = "avatarstroop.vive.verified-presence",
        OpenxrExtensionStrings = "XR_EXT_user_presence")]
#endif
    public sealed class ViveVerifiedPresenceFeature : OpenXRFeature
    {
        // Capability is not a current presence reading. The rig must separately
        // observe absent then present in this session, or require a fresh trigger.
        public bool CapabilityVerified { get; private set; }
        public uint SessionEpoch { get; private set; }

        private ulong instance;
        private ulong systemId;
        private GetSystemProperties getSystemProperties;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetInstanceProcAddr(ulong xrInstance,
            [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetSystemProperties(ulong xrInstance,
            ulong xrSystem, ref SystemProperties properties);

        [StructLayout(LayoutKind.Sequential)]
        private struct UserPresenceProperties
        {
            public int type;
            public IntPtr next;
            public uint supportsUserPresence;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemProperties
        {
            public int type;
            public IntPtr next;
            public ulong systemId;
            public uint vendorId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256,
                ArraySubType = UnmanagedType.U1)]
            public byte[] systemName;
            public uint maxSwapchainImageHeight;
            public uint maxSwapchainImageWidth;
            public uint maxLayerCount;
            public uint orientationTracking;
            public uint positionTracking;
        }

        // Pure managed ABI checks. No runtime loading, XR calls, or native probe.
        public static bool HasExpectedWindowsX64Layout()
        {
            return IntPtr.Size == 8
                && Marshal.SizeOf<UserPresenceProperties>() == 24
                && Marshal.OffsetOf<UserPresenceProperties>("next").ToInt32() == 8
                && Marshal.OffsetOf<UserPresenceProperties>("supportsUserPresence").ToInt32() == 16
                && Marshal.SizeOf<SystemProperties>() == 304
                && Marshal.OffsetOf<SystemProperties>("next").ToInt32() == 8
                && Marshal.OffsetOf<SystemProperties>("systemId").ToInt32() == 16
                && Marshal.OffsetOf<SystemProperties>("systemName").ToInt32() == 28
                && Marshal.OffsetOf<SystemProperties>("maxSwapchainImageHeight").ToInt32() == 284
                && Marshal.OffsetOf<SystemProperties>("orientationTracking").ToInt32() == 296;
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            CapabilityVerified = false;
            instance = xrInstance;
            systemId = 0;
            getSystemProperties = null;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                if (!HasExpectedWindowsX64Layout()
                    || !OpenXRRuntime.IsExtensionEnabled("XR_EXT_user_presence")
                    || xrGetInstanceProcAddr == IntPtr.Zero)
                    return true;

                var resolve = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddr>(xrGetInstanceProcAddr);
                IntPtr pointer;
                if (resolve(instance, "xrGetSystemProperties", out pointer) == 0
                    && pointer != IntPtr.Zero)
                    getSystemProperties = Marshal.GetDelegateForFunctionPointer<GetSystemProperties>(pointer);
            }
            catch (Exception exception) when (IsRecoverableInteropException(exception))
            {
                getSystemProperties = null;
            }
#endif
            // Optional: unsupported presence must not prevent XR initialization.
            return true;
        }

        protected override void OnSystemChange(ulong xrSystem)
        {
            systemId = xrSystem;
            QueryCapability();
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            AdvanceSessionEpoch();
            QueryCapability();
        }

        protected override void OnSessionBegin(ulong xrSession)
        {
            AdvanceSessionEpoch();
            QueryCapability();
        }

        protected override void OnSessionEnd(ulong xrSession)
        {
            CapabilityVerified = false;
            AdvanceSessionEpoch();
        }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            CapabilityVerified = false;
            AdvanceSessionEpoch();
        }

        protected override void OnInstanceDestroy(ulong xrInstance)
        {
            CapabilityVerified = false;
            getSystemProperties = null;
            instance = 0;
            systemId = 0;
            AdvanceSessionEpoch();
        }

        private void AdvanceSessionEpoch()
        {
            unchecked { SessionEpoch++; }
        }

        private void QueryCapability()
        {
            CapabilityVerified = false;
            if (instance == 0 || systemId == 0 || getSystemProperties == null)
                return;

            IntPtr extension = IntPtr.Zero;
            try
            {
                extension = Marshal.AllocHGlobal(Marshal.SizeOf<UserPresenceProperties>());
                Marshal.StructureToPtr(new UserPresenceProperties {
                    type = 1000470001, next = IntPtr.Zero, supportsUserPresence = 0
                }, extension, false);
                var properties = new SystemProperties {
                    type = 5, next = extension, systemName = new byte[256]
                };
                if (getSystemProperties(instance, systemId, ref properties) == 0)
                    CapabilityVerified = Marshal.PtrToStructure<UserPresenceProperties>(extension)
                        .supportsUserPresence == 1;
            }
            catch (Exception exception) when (IsRecoverableInteropException(exception))
            {
                CapabilityVerified = false;
            }
            finally
            {
                if (extension != IntPtr.Zero)
                    Marshal.FreeHGlobal(extension);
            }
        }

        private static bool IsRecoverableInteropException(Exception exception)
        {
            // Do not hide memory corruption or resource exhaustion.
            return exception is ArgumentException
                || exception is MarshalDirectiveException
                || exception is NotSupportedException
                || exception is TypeLoadException
                || exception is DllNotFoundException
                || exception is EntryPointNotFoundException;
        }
    }
}
#endif
