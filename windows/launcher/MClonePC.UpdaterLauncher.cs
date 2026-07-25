using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("MClonePC Updater")]
[assembly: AssemblyDescription("Inicializador executável do atualizador MClonePC")]
[assembly: AssemblyCompany("MClonePC Learning Project")]
[assembly: AssemblyProduct("MClonePC")]
[assembly: AssemblyCopyright("Código autoral do projeto de aprendizado")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MClonePC.Portable
{
    internal static class UpdaterLauncherProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = false;
            List<string> forwardedArguments = new List<string>();
            foreach (string argument in args)
            {
                if (String.Equals(
                    argument,
                    "--silent",
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    silent = true;
                }
                else
                {
                    forwardedArguments.Add(argument);
                }
            }

            string installRoot = AppDomain.CurrentDomain.BaseDirectory;
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

            if (!File.Exists(updaterScript))
            {
                return Finish(
                    2,
                    "O componente de atualização não foi encontrado.\r\n\r\n" +
                    updaterScript,
                    silent
                );
            }
            if (!File.Exists(powershell))
            {
                return Finish(
                    2,
                    "O Windows PowerShell não foi encontrado.\r\n\r\n" +
                    powershell,
                    silent
                );
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = powershell;
                startInfo.WorkingDirectory = installRoot;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.Arguments = BuildPowerShellArguments(
                    updaterScript,
                    installRoot,
                    forwardedArguments
                );

                StringBuilder standardOutput = new StringBuilder();
                StringBuilder standardError = new StringBuilder();

                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += delegate(
                        object sender,
                        DataReceivedEventArgs eventArgs
                    )
                    {
                        if (eventArgs.Data != null)
                        {
                            standardOutput.AppendLine(eventArgs.Data);
                        }
                    };
                    process.ErrorDataReceived += delegate(
                        object sender,
                        DataReceivedEventArgs eventArgs
                    )
                    {
                        if (eventArgs.Data != null)
                        {
                            standardError.AppendLine(eventArgs.Data);
                        }
                    };

                    if (!process.Start())
                    {
                        return Finish(
                            2,
                            "O Windows não iniciou o atualizador.",
                            silent
                        );
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    process.WaitForExit();

                    int exitCode = process.ExitCode;
                    string output = standardOutput.ToString().Trim();
                    string error = standardError.ToString().Trim();

                    if (exitCode == 0)
                    {
                        string successMessage = output.Length > 0
                            ? output
                            : "Atualização concluída.";
                        return Finish(0, successMessage, silent);
                    }

                    string failureMessage = error.Length > 0
                        ? error
                        : output;
                    if (failureMessage.Length == 0)
                    {
                        failureMessage =
                            "O atualizador terminou com o código " +
                            exitCode.ToString() +
                            ".";
                    }
                    return Finish(exitCode, failureMessage, silent);
                }
            }
            catch (Exception exception)
            {
                return Finish(
                    2,
                    "Não foi possível executar a atualização.\r\n\r\n" +
                    exception.Message,
                    silent
                );
            }
        }

        private static int Finish(int exitCode, string message, bool silent)
        {
            if (!silent)
            {
                MessageBox.Show(
                    message,
                    "Atualizador MClonePC",
                    MessageBoxButtons.OK,
                    exitCode == 0
                        ? MessageBoxIcon.Information
                        : MessageBoxIcon.Error
                );
            }
            return exitCode;
        }

        private static string BuildPowerShellArguments(
            string updaterScript,
            string installRoot,
            IList<string> forwardedArguments
        )
        {
            StringBuilder arguments = new StringBuilder();
            arguments.Append("-NoProfile -ExecutionPolicy Bypass -File ");
            arguments.Append(QuoteArgument(updaterScript));
            arguments.Append(" -InstallRoot ");
            arguments.Append(QuoteArgument(installRoot));

            foreach (string argument in forwardedArguments)
            {
                arguments.Append(' ');
                arguments.Append(QuoteArgument(argument));
            }
            return arguments.ToString();
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
