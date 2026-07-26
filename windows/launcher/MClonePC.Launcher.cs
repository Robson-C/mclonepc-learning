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
            StartupUpdateCoordinator.CheckAndApply(installRoot);

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
            if (String.Equals(
                Environment.GetEnvironmentVariable(
                    "MCLONEPC_CLOUD_BRIDGE_DISABLED"
                ),
                "1",
                StringComparison.Ordinal
            ))
            {
                return;
            }
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

    internal static class StartupUpdateCoordinator
    {
        private const int UpdateAvailableExitCode = 10;
        private const int DefaultCheckTimeoutMilliseconds = 2500;
        private const int UpdateTimeoutMilliseconds = 10 * 60 * 1000;

        internal static void CheckAndApply(string installRoot)
        {
            if (String.Equals(
                Environment.GetEnvironmentVariable(
                    "MCLONEPC_UPDATE_DISABLED"
                ),
                "1",
                StringComparison.Ordinal
            ))
            {
                return;
            }

            string updaterScript = Path.Combine(
                installRoot,
                "updater",
                "MClonePC-Updater.ps1"
            );
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"
            );
            if (!File.Exists(updaterScript) || !File.Exists(powershell))
            {
                return;
            }

            try
            {
                int checkExitCode = RunUpdater(
                    powershell,
                    updaterScript,
                    installRoot,
                    true,
                    GetCheckTimeoutMilliseconds(),
                    false
                );
                if (checkExitCode != UpdateAvailableExitCode)
                {
                    return;
                }

                int updateExitCode = RunUpdater(
                    powershell,
                    updaterScript,
                    installRoot,
                    false,
                    UpdateTimeoutMilliseconds,
                    true
                );
                if (updateExitCode != 0)
                {
                    WriteLog(
                        installRoot,
                        "Atualização automática terminou com código " +
                        updateExitCode.ToString() +
                        "; iniciando a versão instalada."
                    );
                }
            }
            catch (Exception exception)
            {
                WriteLog(
                    installRoot,
                    "Verificação automática ignorada: " + exception.Message
                );
            }
        }

        private static int RunUpdater(
            string powershell,
            string updaterScript,
            string installRoot,
            bool checkOnly,
            int timeoutMilliseconds,
            bool showUpdateWindow
        )
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = powershell;
            startInfo.WorkingDirectory = installRoot;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.Arguments = BuildUpdaterArguments(
                updaterScript,
                installRoot,
                checkOnly
            );

            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            using (Process process = new Process())
            using (
                Form updateWindow = showUpdateWindow
                    ? CreateUpdateWindow()
                    : null
            )
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += delegate(
                    object sender,
                    DataReceivedEventArgs eventArgs
                )
                {
                    if (eventArgs.Data != null)
                    {
                        lock (output)
                        {
                            output.AppendLine(eventArgs.Data);
                        }
                    }
                };
                process.ErrorDataReceived += delegate(
                    object sender,
                    DataReceivedEventArgs eventArgs
                )
                {
                    if (eventArgs.Data != null)
                    {
                        lock (error)
                        {
                            error.AppendLine(eventArgs.Data);
                        }
                    }
                };

                if (!process.Start())
                {
                    return 2;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (updateWindow != null)
                {
                    updateWindow.Show();
                    updateWindow.Refresh();
                }

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                    timeoutMilliseconds
                );
                while (
                    !process.WaitForExit(50) &&
                    DateTime.UtcNow < deadline
                )
                {
                    if (updateWindow != null)
                    {
                        Application.DoEvents();
                    }
                }
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }
                    WriteLog(
                        installRoot,
                        checkOnly
                            ? "Verificação excedeu o limite e foi ignorada."
                            : "Atualização excedeu o limite e foi interrompida."
                    );
                    return 3;
                }

                process.WaitForExit();
                if (updateWindow != null)
                {
                    updateWindow.Close();
                }
                if (process.ExitCode != 0 &&
                    process.ExitCode != UpdateAvailableExitCode)
                {
                    string details;
                    lock (error)
                    {
                        details = error.ToString().Trim();
                    }
                    if (details.Length == 0)
                    {
                        lock (output)
                        {
                            details = output.ToString().Trim();
                        }
                    }
                    if (details.Length > 0)
                    {
                        WriteLog(installRoot, details);
                    }
                }
                return process.ExitCode;
            }
        }

        private static Form CreateUpdateWindow()
        {
            Form form = new Form();
            form.Text = "MClonePC";
            form.BackColor = System.Drawing.Color.Black;
            form.ForeColor = System.Drawing.Color.White;
            form.FormBorderStyle = FormBorderStyle.None;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ClientSize = new System.Drawing.Size(432, 640);
            form.TopMost = true;
            form.ShowInTaskbar = true;

            Label label = new Label();
            label.Text = "Atualizando...";
            label.Dock = DockStyle.Fill;
            label.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            label.Font = new System.Drawing.Font(
                "Segoe UI",
                18.0f,
                System.Drawing.FontStyle.Regular
            );
            label.ForeColor = System.Drawing.Color.White;
            label.BackColor = System.Drawing.Color.Black;
            form.Controls.Add(label);
            return form;
        }

        private static string BuildUpdaterArguments(
            string updaterScript,
            string installRoot,
            bool checkOnly
        )
        {
            StringBuilder arguments = new StringBuilder();
            arguments.Append("-NoProfile -ExecutionPolicy Bypass -File ");
            arguments.Append(QuoteArgument(updaterScript));
            arguments.Append(" -InstallRoot ");
            arguments.Append(QuoteArgument(installRoot));

            string manifestPath = Environment.GetEnvironmentVariable(
                "MCLONEPC_UPDATE_MANIFEST_PATH"
            );
            string artifactDirectory = Environment.GetEnvironmentVariable(
                "MCLONEPC_UPDATE_ARTIFACT_DIRECTORY"
            );
            string manifestUrl = Environment.GetEnvironmentVariable(
                "MCLONEPC_UPDATE_MANIFEST_URL"
            );
            if (!String.IsNullOrWhiteSpace(manifestPath))
            {
                arguments.Append(" -ManifestPath ");
                arguments.Append(QuoteArgument(manifestPath));
            }
            else if (!String.IsNullOrWhiteSpace(manifestUrl))
            {
                arguments.Append(" -ManifestUrl ");
                arguments.Append(QuoteArgument(manifestUrl));
            }
            if (!String.IsNullOrWhiteSpace(artifactDirectory))
            {
                arguments.Append(" -ArtifactDirectory ");
                arguments.Append(QuoteArgument(artifactDirectory));
            }
            if (checkOnly)
            {
                arguments.Append(" -CheckOnly");
            }
            return arguments.ToString();
        }

        private static int GetCheckTimeoutMilliseconds()
        {
            int configured;
            if (
                Int32.TryParse(
                    Environment.GetEnvironmentVariable(
                        "MCLONEPC_UPDATE_CHECK_TIMEOUT_MS"
                    ),
                    out configured
                ) &&
                configured >= 500 &&
                configured <= 5000
            )
            {
                return configured;
            }
            return DefaultCheckTimeoutMilliseconds;
        }

        private static void WriteLog(string installRoot, string message)
        {
            try
            {
                string stateDirectory = Path.Combine(installRoot, "state");
                Directory.CreateDirectory(stateDirectory);
                File.AppendAllText(
                    Path.Combine(stateDirectory, "updater.log"),
                    DateTime.UtcNow.ToString("o") +
                    " STARTUP " +
                    message +
                    Environment.NewLine,
                    Encoding.UTF8
                );
            }
            catch
            {
            }
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
