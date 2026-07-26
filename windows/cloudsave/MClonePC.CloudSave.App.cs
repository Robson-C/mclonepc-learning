using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("MClonePC Save em Nuvem")]
[assembly: AssemblyDescription(
    "Sincronizador externo de save do MClonePC com Google Drive"
)]
[assembly: AssemblyCompany("MClonePC Learning Project")]
[assembly: AssemblyProduct("MClonePC")]
[assembly: AssemblyCopyright("Código autoral do projeto de aprendizado")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MClonePC.CloudSave
{
    internal static class CloudSaveProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12;
            try
            {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                CloudSaveConfig config = CloudSaveConfig.Load(
                    Path.Combine(root, "cloud-save.json")
                );
                if (
                    args.Length > 0 &&
                    String.Equals(
                        args[0],
                        "--bridge",
                        StringComparison.Ordinal
                    )
                )
                {
                    return CloudSaveBridge.Run(
                        root,
                        config,
                        args.Skip(1).ToArray()
                    );
                }
                if (
                    args.Length > 0 &&
                    String.Equals(
                        args[0],
                        "--bridge-server",
                        StringComparison.Ordinal
                    )
                )
                {
                    return CloudSaveBridge.RunServer(
                        root,
                        config,
                        args.Skip(1).ToArray()
                    );
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new CloudSaveForm(root, config));
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "MClonePC Save em Nuvem",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return 2;
            }
        }
    }

    internal sealed class CloudSaveForm : Form
    {
        private readonly string installRoot;
        private readonly CloudSaveConfig config;
        private readonly string saveDirectory;
        private readonly string workDirectory;
        private readonly string backupDirectory;
        private readonly OAuthTokenStore tokenStore;
        private readonly GoogleOAuthClient oauth;
        private readonly GoogleDriveAppDataClient drive;
        private readonly SaveBundleService bundles;
        private readonly RestoreService restore;
        private readonly CancellationTokenSource cancellation;

        private Label connectionValue;
        private Label localValue;
        private Label cloudValue;
        private Label activityValue;
        private Button connectButton;
        private Button disconnectButton;
        private Button refreshButton;
        private Button uploadButton;
        private Button restoreButton;
        private bool busy;

        public CloudSaveForm(
            string root,
            CloudSaveConfig configuration
        )
        {
            installRoot = root;
            config = configuration;
            saveDirectory = config.GetExpandedSaveDirectory();
            workDirectory = Path.Combine(installRoot, "work", "cloud-save");
            backupDirectory = Path.Combine(
                installRoot,
                "backups",
                "cloud-save"
            );
            string stateDirectory = CloudSaveRuntime.GetStateDirectory();
            tokenStore = new OAuthTokenStore(stateDirectory);
            oauth = new GoogleOAuthClient(config, tokenStore);
            drive = new GoogleDriveAppDataClient(oauth);
            bundles = new SaveBundleService(
                CloudSaveRuntime.GetOrCreateDeviceId(stateDirectory)
            );
            restore = new RestoreService();
            cancellation = new CancellationTokenSource();

            BuildInterface();
            UpdateConnectionState();
            UpdateLocalState();
        }

        private void BuildInterface()
        {
            Text = "MClonePC — Save em Nuvem";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 390);
            MinimumSize = new Size(636, 429);
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(245, 242, 232);

            Label title = new Label();
            title.Text = "Save em Nuvem";
            title.Font = new Font("Segoe UI Semibold", 20F);
            title.AutoSize = true;
            title.Location = new Point(24, 18);
            Controls.Add(title);

            Label explanation = new Label();
            explanation.Text =
                "O jogo continua usando o save local vanilla. Enviar e " +
                "restaurar são ações manuais.";
            explanation.AutoSize = false;
            explanation.Size = new Size(570, 42);
            explanation.Location = new Point(27, 62);
            Controls.Add(explanation);

            AddStatusRow("Conta Google:", 112, out connectionValue);
            AddStatusRow("Save local:", 148, out localValue);
            AddStatusRow("Save na nuvem:", 184, out cloudValue);

            connectButton = AddButton(
                "Conectar Google",
                26,
                230,
                150,
                ConnectClicked
            );
            disconnectButton = AddButton(
                "Desconectar",
                186,
                230,
                125,
                DisconnectClicked
            );
            refreshButton = AddButton(
                "Atualizar status",
                321,
                230,
                135,
                RefreshClicked
            );
            uploadButton = AddButton(
                "Enviar save",
                26,
                278,
                205,
                UploadClicked
            );
            restoreButton = AddButton(
                "Restaurar save",
                241,
                278,
                205,
                RestoreClicked
            );

            activityValue = new Label();
            activityValue.Text = "Pronto.";
            activityValue.AutoSize = false;
            activityValue.BorderStyle = BorderStyle.FixedSingle;
            activityValue.BackColor = Color.White;
            activityValue.Location = new Point(27, 336);
            activityValue.Size = new Size(566, 32);
            activityValue.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(activityValue);

            FormClosing += delegate
            {
                cancellation.Cancel();
            };
            FormClosed += delegate
            {
                drive.Dispose();
                oauth.Dispose();
                cancellation.Dispose();
            };
        }

        private void AddStatusRow(
            string caption,
            int y,
            out Label value
        )
        {
            Label label = new Label();
            label.Text = caption;
            label.Font = new Font("Segoe UI Semibold", 10F);
            label.AutoSize = false;
            label.Location = new Point(27, y);
            label.Size = new Size(135, 26);
            label.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(label);

            value = new Label();
            value.Text = "—";
            value.AutoSize = false;
            value.Location = new Point(165, y);
            value.Size = new Size(428, 26);
            value.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(value);
        }

        private Button AddButton(
            string text,
            int x,
            int y,
            int width,
            EventHandler handler
        )
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, 38);
            button.Click += handler;
            Controls.Add(button);
            return button;
        }

        private async void ConnectClicked(object sender, EventArgs e)
        {
            await RunBusyAsync(
                "Aguardando autorização no navegador...",
                async delegate
                {
                    await oauth.ConnectAsync(cancellation.Token);
                    UpdateConnectionState();
                    await RefreshCloudStateAsync();
                    MessageBox.Show(
                        "Conta conectada. O token de renovação foi protegido " +
                        "pelo Windows para este usuário.",
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            );
        }

        private void DisconnectClicked(object sender, EventArgs e)
        {
            if (
                MessageBox.Show(
                    "Remover a conexão Google deste computador?",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                ) != DialogResult.Yes
            )
            {
                return;
            }
            oauth.Disconnect();
            cloudValue.Text = "—";
            UpdateConnectionState();
            activityValue.Text = "Conta desconectada.";
        }

        private async void RefreshClicked(object sender, EventArgs e)
        {
            await RunBusyAsync(
                "Consultando o estado local e a nuvem...",
                async delegate
                {
                    UpdateLocalState();
                    await RefreshCloudStateAsync();
                }
            );
        }

        private async void UploadClicked(object sender, EventArgs e)
        {
            await RunBusyAsync(
                "Preparando o save para envio...",
                async delegate
                {
                    EnsureGameClosed();
                    string fingerprint =
                        bundles.ComputeDirectoryFingerprint(saveDirectory);
                    if (String.IsNullOrWhiteSpace(fingerprint))
                    {
                        throw new InvalidOperationException(
                            "Nenhum save local foi encontrado."
                        );
                    }

                    RemoteSaveFile remote = await drive.FindAsync(
                        config.remote_file_name,
                        cancellation.Token
                    );
                    if (
                        remote != null &&
                        !String.Equals(
                            remote.Fingerprint,
                            fingerprint,
                            StringComparison.OrdinalIgnoreCase
                        ) &&
                        MessageBox.Show(
                            "Já existe um save diferente na nuvem.\r\n\r\n" +
                            "Deseja substituí-lo pelo save local?",
                            Text,
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning
                        ) != DialogResult.Yes
                    )
                    {
                        activityValue.Text = "Envio cancelado.";
                        return;
                    }
                    if (
                        remote != null &&
                        String.Equals(
                            remote.Fingerprint,
                            fingerprint,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        activityValue.Text =
                            "O save local já é igual ao da nuvem.";
                        return;
                    }

                    int revision = remote == null
                        ? 1
                        : Math.Max(1, remote.Revision + 1);
                    CreatedSaveBundle bundle = bundles.Create(
                        saveDirectory,
                        workDirectory,
                        revision
                    );
                    try
                    {
                        activityValue.Text = "Enviando para o Google Drive...";
                        RemoteSaveFile uploaded = await drive.UploadAsync(
                            config.remote_file_name,
                            remote == null ? null : remote.Id,
                            File.ReadAllBytes(bundle.FilePath),
                            bundle.BundleSha256,
                            bundle.SaveFingerprint,
                            bundle.Revision,
                            cancellation.Token
                        );
                        cloudValue.Text = FormatRemote(uploaded);
                        activityValue.Text =
                            "Save enviado e verificado pela resposta do Drive.";
                        MessageBox.Show(
                            "Save enviado com sucesso.",
                            Text,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                    }
                    finally
                    {
                        if (File.Exists(bundle.FilePath))
                        {
                            File.Delete(bundle.FilePath);
                        }
                    }
                }
            );
        }

        private async void RestoreClicked(object sender, EventArgs e)
        {
            await RunBusyAsync(
                "Consultando o save na nuvem...",
                async delegate
                {
                    EnsureGameClosed();
                    RemoteSaveFile remote = await drive.FindAsync(
                        config.remote_file_name,
                        cancellation.Token
                    );
                    if (remote == null)
                    {
                        throw new InvalidOperationException(
                            "Nenhum save foi encontrado na nuvem."
                        );
                    }

                    string localFingerprint =
                        bundles.ComputeDirectoryFingerprint(saveDirectory);
                    if (
                        String.Equals(
                            localFingerprint,
                            remote.Fingerprint,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        activityValue.Text =
                            "O save local já é igual ao da nuvem.";
                        return;
                    }

                    if (
                        MessageBox.Show(
                            "Restaurar o save da nuvem?\r\n\r\n" +
                            "O save local atual será preservado em backup. " +
                            "O jogo deve permanecer fechado.",
                            Text,
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning
                        ) != DialogResult.Yes
                    )
                    {
                        activityValue.Text = "Restauração cancelada.";
                        return;
                    }

                    Directory.CreateDirectory(workDirectory);
                    string downloadPath = Path.Combine(
                        workDirectory,
                        "download-" + Guid.NewGuid().ToString("N") + ".zip"
                    );
                    ValidatedSaveBundle validated = null;
                    try
                    {
                        byte[] content = await drive.DownloadAsync(
                            remote.Id,
                            cancellation.Token
                        );
                        File.WriteAllBytes(downloadPath, content);
                        activityValue.Text =
                            "Validando hashes antes de restaurar...";
                        validated = bundles.ValidateAndExtract(
                            downloadPath,
                            workDirectory,
                            remote.BundleSha256
                        );
                        string backup = restore.Restore(
                            validated,
                            saveDirectory,
                            backupDirectory
                        );
                        validated = null;
                        UpdateLocalState();
                        activityValue.Text = String.IsNullOrWhiteSpace(backup)
                            ? "Save restaurado; não havia save local anterior."
                            : "Save restaurado; backup local criado.";
                        MessageBox.Show(
                            "Save restaurado com sucesso." +
                            (String.IsNullOrWhiteSpace(backup)
                                ? ""
                                : "\r\n\r\nBackup: " + backup),
                            Text,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                    }
                    finally
                    {
                        if (
                            validated != null &&
                            Directory.Exists(validated.ExtractionRoot)
                        )
                        {
                            Directory.Delete(
                                validated.ExtractionRoot,
                                true
                            );
                        }
                        if (File.Exists(downloadPath))
                        {
                            File.Delete(downloadPath);
                        }
                    }
                }
            );
        }

        private async Task RefreshCloudStateAsync()
        {
            if (!oauth.HasStoredConnection())
            {
                cloudValue.Text = "Conecte uma conta para consultar.";
                return;
            }
            RemoteSaveFile remote = await drive.FindAsync(
                config.remote_file_name,
                cancellation.Token
            );
            cloudValue.Text = remote == null
                ? "Nenhum save enviado."
                : FormatRemote(remote);
        }

        private void UpdateConnectionState()
        {
            bool connected = oauth.HasStoredConnection();
            connectionValue.Text = connected
                ? "Conectada neste usuário do Windows"
                : "Não conectada";
            connectButton.Enabled = !connected && !busy;
            disconnectButton.Enabled = connected && !busy;
            refreshButton.Enabled = connected && !busy;
            uploadButton.Enabled = connected && !busy;
            restoreButton.Enabled = connected && !busy;
        }

        private void UpdateLocalState()
        {
            string fingerprint =
                bundles.ComputeDirectoryFingerprint(saveDirectory);
            if (String.IsNullOrWhiteSpace(fingerprint))
            {
                localValue.Text = "Nenhum save encontrado.";
                return;
            }
            DirectoryInfo directory = new DirectoryInfo(saveDirectory);
            FileInfo[] files = directory.GetFiles(
                "*",
                SearchOption.AllDirectories
            );
            DateTime newest = files
                .Max(item => item.LastWriteTimeUtc)
                .ToLocalTime();
            localValue.Text = files.Length + " arquivo(s), alterado em " +
                newest.ToString("dd/MM/yyyy HH:mm:ss");
        }

        private static string FormatRemote(RemoteSaveFile remote)
        {
            string date = remote.ModifiedTimeUtc.HasValue
                ? remote.ModifiedTimeUtc.Value.ToLocalTime()
                    .ToString("dd/MM/yyyy HH:mm:ss")
                : "data desconhecida";
            return "revisão " + remote.Revision + ", " + date;
        }

        private void EnsureGameClosed()
        {
            if (CloudSaveRuntime.IsGameRunning())
            {
                throw new InvalidOperationException(
                    "Feche o MClonePC antes de enviar ou restaurar o save."
                );
            }
        }

        private async Task RunBusyAsync(
            string activity,
            Func<Task> action
        )
        {
            if (busy)
            {
                return;
            }
            busy = true;
            activityValue.Text = activity;
            UpdateConnectionState();
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                activityValue.Text = "Operação cancelada.";
            }
            catch (Exception exception)
            {
                activityValue.Text = "Falha: " + exception.Message;
                MessageBox.Show(
                    exception.Message,
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                busy = false;
                UpdateConnectionState();
            }
        }
    }
}
