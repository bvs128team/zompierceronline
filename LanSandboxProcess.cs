using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ZompiercerLAN
{
    // Windows is the security boundary. No fallback to an ordinary process is permitted.
    internal sealed class LanSandboxProcess : IDisposable
    {
        internal Stream Input { get; private set; }
        internal Stream Output { get; private set; }
        internal int ProcessId { get; private set; }
        private SafeKernelHandle process, job;
        private readonly List<IDisposable> locks = new List<IDisposable>();
        private readonly object gate = new object();
        private bool disposed;
        // Exactly one network capability per worker: LAN modes get privateNetworkClientServer,
        // relay modes get internetClient (outbound only). Never both.
        private const string PrivateCapability = "S-1-15-3-3", InternetCapability = "S-1-15-3-1";
        private const uint JobFlags = 0x2000 | 0x100 | 0x8; // kill-on-close, process memory, one process
        private const ulong MemoryLimit = 256UL * 1024 * 1024;
        // PROCESS_CREATION_MITIGATION_POLICY_*_ALWAYS_ON: extension points,
        // remote images, low-label images, System32 preference. Keep CLR JIT available.
        // Win32k lockdown prevents this .NET Framework WinExe from initializing (0x8007045A).
        private const ulong Mitigations = (1UL << 32) | (1UL << 52) | (1UL << 56) | (1UL << 60);
        internal bool HasExited { get { lock (gate) { return disposed || process == null || WaitForSingleObject(process, 0) == 0; } } }

        internal static LanSandboxProcess Start(string directory, string executableHash, string cryptoHash, string configHash, bool internet = false)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8)
                throw new PlatformNotSupportedException("LAN sandbox requires 64-bit Windows.");
            using (var token = OpenToken(GetCurrentProcess()))
                if (TokenInt(token, 20) != 0) throw new SecurityException("Do not run the game elevated.");
            string dir = ValidateDirectory(directory);
            string capability = internet ? InternetCapability : PrivateCapability;
            var result = new LanSandboxProcess();
            IntPtr sid = IntPtr.Zero, cap = IntPtr.Zero, attributes = IntPtr.Zero;
            bool attributesInitialized = false;
            var allocations = new List<IntPtr>();
            SafeFileHandle childRead = null, childWrite = null, parentRead = null, parentWrite = null;
            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
            try
            {
                // Pin both content and directory entries while Windows maps the executable and dependencies.
                var dirLock = CreateFile(dir, 0x80, 1, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (dirLock.IsInvalid) { dirLock.Dispose(); Fail("Lock helper directory"); }
                result.locks.Add(dirLock);
                string[] names = { "ZompiercerLAN.Network.exe", "BouncyCastle.Cryptography.dll", "ZompiercerLAN.Network.exe.config" };
                string[] hashes = { executableHash, cryptoHash, configHash };
                for (int i = 0; i < names.Length; i++)
                {
                    string path = Path.Combine(dir, names[i]);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new SecurityException("Reparse helper asset.");
                    var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    result.locks.Add(file);
                    using (var sha = SHA256.Create())
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                        if (hashes[i] == null || hashes[i].Length != 64 || !String.Equals(actual, hashes[i], StringComparison.OrdinalIgnoreCase))
                            throw new SecurityException("Helper integrity mismatch: " + names[i]);
                    }
                }
                string profile = ProfileName(dir);
                int hr = CreateAppContainerProfile(profile, profile, "Zompiercer LAN network isolation", IntPtr.Zero, 0, out sid);
                if (hr == unchecked((int)0x800700B7)) hr = DeriveAppContainerSidFromAppContainerName(profile, out sid);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                var identity = new SecurityIdentifier(sid);
                GrantRead(dir, identity, true);
                foreach (string name in names) GrantRead(Path.Combine(dir, name), identity, false);
                Check(ConvertStringSidToSid(capability, out cap), "Create network capability SID");

                result.job = CreateJobObject(IntPtr.Zero, null);
                if (result.job.IsInvalid) Fail("Create job");
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JobFlags;
                limits.BasicLimitInformation.ActiveProcessLimit = 1;
                limits.ProcessMemoryLimit = new UIntPtr(MemoryLimit);
                SetJob(result.job, 9, limits);
                SetJob(result.job, 15, new CPU_LIMIT { Flags = 5, Rate = 2000 });
                VerifyJob(result.job);

                SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), InheritHandle = true };
                Check(CreatePipe(out childRead, out parentWrite, ref sa, 4096), "Create command pipe");
                Check(SetHandleInformation(parentWrite, 1, 0), "Protect parent command handle");
                Check(CreatePipe(out parentRead, out childWrite, ref sa, 4096), "Create event pipe");
                Check(SetHandleInformation(parentRead, 1, 0), "Protect parent event handle");
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 4, 0, ref size);
                if (size == IntPtr.Zero) Fail("Size startup attributes");
                attributes = Marshal.AllocHGlobal(size);
                Check(InitializeProcThreadAttributeList(attributes, 4, 0, ref size), "Initialize startup attributes");
                attributesInitialized = true;
                var caps = new SID_AND_ATTRIBUTES { Sid = cap, Attributes = 4 };
                IntPtr capsMem = Allocate(caps, allocations);
                var security = new SECURITY_CAPABILITIES { AppContainerSid = sid, Capabilities = capsMem, CapabilityCount = 1 };
                Attribute(attributes, 0x20009, Allocate(security, allocations), Marshal.SizeOf(typeof(SECURITY_CAPABILITIES)));
                IntPtr handles = Marshal.AllocHGlobal(2 * IntPtr.Size); allocations.Add(handles);
                Marshal.WriteIntPtr(handles, childRead.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size, childWrite.DangerousGetHandle());
                Attribute(attributes, 0x20002, handles, 2 * IntPtr.Size);
                Attribute(attributes, 0x2000E, Allocate((uint)1, allocations), 4); // CHILD_PROCESS_RESTRICTED
                Attribute(attributes, 0x20007, Allocate(Mitigations, allocations), 8); // MITIGATION_POLICY
                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                si.AttributeList = attributes;
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (String.IsNullOrEmpty(windows) || windows.IndexOf('\0') >= 0) throw new SecurityException("Invalid Windows directory.");
                IntPtr profilePath;
                hr = GetAppContainerFolderPath(identity.Value, out profilePath);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                string privatePath;
                try { privatePath = Path.Combine(Marshal.PtrToStringUni(profilePath), "AC"); }
                finally { Marshal.FreeCoTaskMem(profilePath); }
                // Windows rewrites these three variables when creating the AppContainer.
                // Supply only the profile's own paths, never the caller's environment.
                IntPtr environment = Marshal.StringToHGlobalUni("LOCALAPPDATA=" + privatePath + "\0SystemRoot=" + windows + "\0TEMP=" + Path.Combine(privatePath, "Temp") + "\0TMP=" + Path.Combine(privatePath, "Temp") + "\0WINDIR=" + windows + "\0\0");
                allocations.Add(environment);
                string exe = Path.Combine(dir, names[0]);
                var command = new StringBuilder("\"" + exe + "\" " + childRead.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture) + " " + childWrite.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture));
                Check(CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 0x00080000 | 0x400 | 4,
                    environment, dir, ref si, out pi), "Create suspended AppContainer");
                result.process = new SafeKernelHandle(pi.Process); pi.Process = IntPtr.Zero;
                result.ProcessId = unchecked((int)pi.ProcessId);
                Check(AssignProcessToJobObject(result.job, result.process), "Assign sandbox job");
                bool inJob;
                Check(IsProcessInJob(result.process.DangerousGetHandle(), result.job.DangerousGetHandle(), out inJob), "Verify job membership");
                if (!inJob) throw new SecurityException("Sandbox job membership missing.");
                using (var token = OpenToken(result.process.DangerousGetHandle())) VerifyToken(token, identity.Value, capability);
                VerifyJob(result.job);
                VerifyMitigations(result.process.DangerousGetHandle());
                result.Input = new FileStream(parentWrite, FileAccess.Write, 4096, false); parentWrite = null;
                result.Output = new FileStream(parentRead, FileAccess.Read, 4096, false); parentRead = null;
                if (ResumeThread(pi.Thread) == UInt32.MaxValue) Fail("Resume sandbox");
                return result;
            }
            catch { result.Dispose(); throw; }
            finally
            {
                if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
                if (pi.Process != IntPtr.Zero) { TerminateProcess(pi.Process, 1); CloseHandle(pi.Process); }
                if (childRead != null) childRead.Dispose();
                if (childWrite != null) childWrite.Dispose();
                if (parentRead != null) parentRead.Dispose();
                if (parentWrite != null) parentWrite.Dispose();
                if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                foreach (IntPtr memory in allocations) Marshal.FreeHGlobal(memory);
                if (sid != IntPtr.Zero) FreeSid(sid);
                if (cap != IntPtr.Zero) LocalFree(cap);
            }
        }

        // Returns true when this worker holds internetClient (relay modes), false for LAN.
        internal static bool RequireSandbox()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8) throw new SecurityException("Windows sandbox required.");
            IntPtr sid;
            int hr = DeriveAppContainerSidFromAppContainerName(ProfileName(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)), out sid);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            bool internet;
            try { using (var token = OpenToken(GetCurrentProcess())) { internet = TokenCapability(token) == InternetCapability; VerifyToken(token, new SecurityIdentifier(sid).Value, internet ? InternetCapability : PrivateCapability); } }
            finally { FreeSid(sid); }
            bool inJob;
            Check(IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob), "Inspect current job");
            if (!inJob) throw new SecurityException("Sandbox job required.");
            // NULL means the calling process's immediate job. Parent also verifies its specific job handle.
            VerifyJob(null);
            VerifyMitigations(GetCurrentProcess());
            return internet;
        }

        private static void VerifyMitigations(IntPtr processHandle)
        {
            // PROCESS_MITIGATION_POLICY enum values; each queried structure contains one DWORD.
            RequireMitigation(processHandle, 6, 1); // ProcessExtensionPointDisablePolicy: DisableExtensionPoints
            RequireMitigation(processHandle, 10, 7); // ProcessImageLoadPolicy: NoRemoteImages, NoLowMandatoryLabelImages, PreferSystem32Images
        }
        private static void RequireMitigation(IntPtr processHandle, int policy, uint required)
        {
            uint flags;
            Check(GetProcessMitigationPolicy(processHandle, policy, out flags, new UIntPtr(4)), "Inspect sandbox mitigation " + policy);
            if ((flags & required) != required) throw new SecurityException("Sandbox mitigation missing: " + policy);
        }

        internal void Kill() { Dispose(); }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                // Terminate first: closes the remote ends and releases blocked pipe reads/writes.
                if (process != null && !process.IsInvalid) TerminateProcess(process.DangerousGetHandle(), 1);
                if (job != null) job.Dispose();
                if (process != null && !process.IsInvalid) WaitForSingleObject(process, 5000);
                try { if (Input != null) Input.Dispose(); } catch (IOException) { }
                finally
                {
                    try { if (Output != null) Output.Dispose(); } catch (IOException) { }
                    finally
                    {
                        if (process != null) process.Dispose();
                        foreach (var item in locks) item.Dispose();
                        locks.Clear();
                    }
                }
            }
        }

        private static string ValidateDirectory(string directory)
        {
            string dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (dir.StartsWith("\\\\", StringComparison.Ordinal) || dir.Length < 4 || dir.IndexOf('"') >= 0 || new DriveInfo(Path.GetPathRoot(dir)).DriveType != DriveType.Fixed)
                throw new SecurityException("Helper must reside on a fixed local drive.");
            for (var info = new DirectoryInfo(dir); info != null; info = info.Parent)
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new SecurityException("Reparse helper directory.");
            string[] entries = Directory.GetFileSystemEntries(dir);
            if (entries.Length != 3) throw new SecurityException("Helper directory must contain exactly the three pinned runtime assets.");
            return dir;
        }
        private static string ProfileName(string dir)
        {
            using (var sha = SHA256.Create()) return "ZompiercerLAN." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(dir.ToUpperInvariant()))).Replace("-", "").Substring(0, 40);
        }
        private static void GrantRead(string path, SecurityIdentifier sid, bool directory)
        {
            FileSystemSecurity acl = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
            // Replace any explicit grant for this exact profile; never grant to all app packages.
            acl.PurgeAccessRules(sid);
            // .NET Framework adds Synchronize to allow rules implicitly; Unity Mono
            // does not. CLR activation needs it for synchronous reads of these assets.
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
            if (directory) Directory.SetAccessControl(path, (DirectorySecurity)acl); else File.SetAccessControl(path, (FileSecurity)acl);
        }
        private static string TokenCapability(SafeKernelHandle token)
        {
            string result = null;
            WithToken(token, 30, p =>
            {
                if (Marshal.ReadInt32(p) != 1) throw new SecurityException("Unexpected capability count.");
                var value = (SID_AND_ATTRIBUTES)Marshal.PtrToStructure(IntPtr.Add(p, IntPtr.Size == 8 ? 8 : 4), typeof(SID_AND_ATTRIBUTES));
                result = new SecurityIdentifier(value.Sid).Value;
            });
            if (result != PrivateCapability && result != InternetCapability) throw new SecurityException("Unexpected network capability.");
            return result;
        }
        private static void VerifyToken(SafeKernelHandle token, string expectedSid, string capability)
        {
            if (TokenInt(token, 29) != 1 || TokenInt(token, 20) != 0) throw new SecurityException("AppContainer/non-elevated token required.");
            WithToken(token, 31, p => { if (new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value != expectedSid) throw new SecurityException("Wrong AppContainer SID."); });
            WithToken(token, 25, p => { if (new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value != "S-1-16-4096") throw new SecurityException("Low integrity required."); });
            WithToken(token, 30, p =>
            {
                if (Marshal.ReadInt32(p) != 1) throw new SecurityException("Unexpected capability count.");
                var value = (SID_AND_ATTRIBUTES)Marshal.PtrToStructure(IntPtr.Add(p, IntPtr.Size == 8 ? 8 : 4), typeof(SID_AND_ATTRIBUTES));
                if (new SecurityIdentifier(value.Sid).Value != capability || (value.Attributes & 4) == 0 || (value.Attributes & 0x10) != 0)
                    throw new SecurityException("Exactly the expected network capability required.");
            });
        }
        private static int TokenInt(SafeKernelHandle token, int kind) { int value = 0; WithToken(token, kind, p => value = Marshal.ReadInt32(p)); return value; }
        private static void WithToken(SafeKernelHandle token, int kind, Action<IntPtr> action)
        {
            int size; GetTokenInformation(token, kind, IntPtr.Zero, 0, out size);
            if (size <= 0 || size > 65536) Fail("Size token information");
            IntPtr memory = Marshal.AllocHGlobal(size);
            try { Check(GetTokenInformation(token, kind, memory, size, out size), "Inspect sandbox token"); action(memory); }
            finally { Marshal.FreeHGlobal(memory); }
        }
        private static SafeKernelHandle OpenToken(IntPtr processHandle) { SafeKernelHandle token; Check(OpenProcessToken(processHandle, 8, out token), "Open process token"); return token; }
        private static void VerifyJob(SafeKernelHandle handle)
        {
            var limits = QueryJob<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(handle, 9);
            var cpu = QueryJob<CPU_LIMIT>(handle, 15);
            if (limits.BasicLimitInformation.LimitFlags != JobFlags || limits.BasicLimitInformation.ActiveProcessLimit != 1 || limits.ProcessMemoryLimit.ToUInt64() != MemoryLimit || cpu.Flags != 5 || cpu.Rate != 2000)
                throw new SecurityException("Sandbox job restrictions mismatch.");
        }
        private static T QueryJob<T>(SafeKernelHandle jobHandle, int kind) where T : struct
        {
            int length = Marshal.SizeOf(typeof(T)); IntPtr p = Marshal.AllocHGlobal(length);
            try { Check(QueryInformationJobObject(jobHandle == null ? IntPtr.Zero : jobHandle.DangerousGetHandle(), kind, p, (uint)length, IntPtr.Zero), "Query sandbox job"); return (T)Marshal.PtrToStructure(p, typeof(T)); }
            finally { Marshal.FreeHGlobal(p); }
        }
        private static void SetJob<T>(SafeKernelHandle jobHandle, int kind, T value) where T : struct
        {
            int length = Marshal.SizeOf(typeof(T)); IntPtr p = Marshal.AllocHGlobal(length);
            try { Marshal.StructureToPtr(value, p, false); Check(SetInformationJobObject(jobHandle, kind, p, (uint)length), "Restrict sandbox job"); }
            finally { Marshal.FreeHGlobal(p); }
        }
        private static IntPtr Allocate<T>(T value, List<IntPtr> allocations) where T : struct { IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T))); allocations.Add(p); Marshal.StructureToPtr(value, p, false); return p; }
        private static void Attribute(IntPtr list, int kind, IntPtr value, int length) { Check(UpdateProcThreadAttribute(list, 0, new IntPtr(kind), value, new IntPtr(length), IntPtr.Zero, IntPtr.Zero), "Set sandbox startup attribute"); }
        private static void Check(bool success, string operation) { if (!success) Fail(operation); }
        private static void Fail(string operation) { int error = Marshal.GetLastWin32Error(); throw new Win32Exception(error, operation + " (Win32 " + error + ")"); }

        private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeKernelHandle() : base(true) { }
            internal SafeKernelHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential)] private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO { public int cb; public string Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError; }
        [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BASIC_LIMIT { public long ProcessUserTimeLimit, JobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public BASIC_LIMIT BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [StructLayout(LayoutKind.Sequential)] private struct CPU_LIMIT { public uint Flags, Rate; }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr caps, uint count, out IntPtr sid);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSid(string text, out IntPtr sid);
        [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint flags, UIntPtr length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeKernelHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeKernelHandle job, int kind, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, IntPtr info, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeKernelHandle job, SafeKernelHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint timeout);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeKernelHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeKernelHandle token, int kind, IntPtr info, int length, out int returned);
    }
}

