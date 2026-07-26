using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("MClonePC")]
[assembly: AssemblyDescription("Inicializador portátil do MClonePC")]
[assembly: AssemblyCompany("MClonePC Learning Project")]
[assembly: AssemblyProduct("MClonePC")]
[assembly: AssemblyCopyright("Código autoral do projeto de aprendizado")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MClonePC.Portable
{
    internal static class LauncherProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string installRoot = AppDomain.CurrentDomain.BaseDirectory;
            string gameDirectory = Path.Combine(installRoot, "game");
            string gameExecutable = Path.Combine(gameDirectory, "mclonepc.exe");

            if (!File.Exists(gameExecutable))
            {
                return ShowError(
                    "O executável do jogo não foi encontrado.\r\n\r\n" +
                    gameExecutable
                );
            }

            try
            {
                StartCloudBridge(installRoot);
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = gameExecutable;
                startInfo.WorkingDirectory = gameDirectory;
                startInfo.UseShellExecute = false;
                startInfo.Arguments = BuildArgumentString(args);

                Process process = Process.Start(startInfo);
                if (process == null)
                {
                    return ShowError("O Windows não iniciou o jogo.");
                }
                return 0;
            }
            catch (Exception exception)
            {
                return ShowError(
                    "Não foi possível iniciar o MClonePC.\r\n\r\n" +
                    exception.Message
                );
            }
        }

        private static void StartCloudBridge(string installRoot)
        {
            string executable = Path.Combine(
                installRoot,
                "MClonePC-Save-Nuvem.exe"
            );
            if (!File.Exists(executable))
            {
                return;
            }
            string stateDirectory = Path.Combine(installRoot, "state");
            Directory.CreateDirectory(stateDirectory);
            string readyPath = Path.Combine(
                stateDirectory,
                "cloud-bridge.ready"
            );
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executable;
            startInfo.WorkingDirectory = installRoot;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.Arguments =
                "--bridge-server --ready " + QuoteArgument(readyPath);

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    return;
                }
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (
                    !File.Exists(readyPath) &&
                    !process.HasExited &&
                    DateTime.UtcNow < deadline
                )
                {
                    Thread.Sleep(25);
                }
            }
        }

        private static int ShowError(string message)
        {
            MessageBox.Show(
                message,
                "MClonePC",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return 2;
        }

        private static string BuildArgumentString(string[] args)
        {
            StringBuilder result = new StringBuilder();
            for (int index = 0; index < args.Length; index++)
            {
                if (index > 0)
                {
                    result.Append(' ');
                }
                result.Append(QuoteArgument(args[index]));
            }
            return result.ToString();
        }

        private static string QuoteArgument(string value)
        {
            if (value.Length == 0)
            {
                return "\"\"";
            }

            bool requiresQuotes = value.IndexOfAny(
                new char[] { ' ', '\t', '\n', '\v', '"' }
            ) >= 0;
            if (!requiresQuotes)
            {
                return value;
            }

            StringBuilder quoted = new StringBuilder();
            quoted.Append('"');
            int backslashCount = 0;

            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', backslashCount * 2 + 1);
                    quoted.Append('"');
                    backslashCount = 0;
                    continue;
                }

                quoted.Append('\\', backslashCount);
                backslashCount = 0;
                quoted.Append(character);
            }

            quoted.Append('\\', backslashCount * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
