using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Resources;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Globalization;

namespace pylorak.TinyWall
{
    internal partial class DevelToolForm : Form
    {
        private static readonly string[] SIGNING_FILE_PATTERNS = new string[] { "*.dll", "*.exe", "*.msi" };

        // Key - The primary resource
        // Value - List of satellite resources
        private readonly List<KeyValuePair<string, string[]>> ResXInputs = new();

        internal DevelToolForm()
        {
            System.Windows.Forms.MessageBox.Show(
                "This tool is not meant for end-users. Only use this tool when instructed to do so by the application developer.",
                "Warning: Not for users!",
                MessageBoxButtons.OK,
                MessageBoxIcon.Exclamation
                );

            InitializeComponent();
        }

        private void btnAssocBrowse_Click(object sender, EventArgs e)
        {
            ofd.Filter = "All files (*)|*";
            if (ofd.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
            {
                txtAssocExePath.Text = ofd.FileName;
            }
        }

        private void btnAssocCreate_Click(object sender, EventArgs e)
        {
            if (File.Exists(txtAssocExePath.Text))
            {
                var exe = new ExecutableSubject(txtAssocExePath.Text);
                var id = new DatabaseClasses.SubjectIdentity(exe) { AllowedSha1 = new List<string> { exe.HashSha1 } };
                if (exe.IsSigned && exe.CertValid)
                {
                    id.CertificateSubjects = new List<string>();
                    if (exe.CertSubject is not null)
                        id.CertificateSubjects.Add(exe.CertSubject);
                }
                var utf8bytes = SerializationHelper.Serialize(id);
                txtAssocResult.Text = Encoding.UTF8.GetString(utf8bytes);
            }
            else
            {
                MessageBox.Show(this, "No such file.", "File not found", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        private void btnProfileFolderBrowse_Click(object sender, EventArgs e)
        {
            if (fbd.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                txtDBFolderPath.Text = fbd.SelectedPath;
        }

        private void btnCollectionsCreate_Click(object sender, EventArgs e)
        {
            string outputPath = Path.Combine(txtAssocOutputPath.Text, "profiles.json");
            string inputPath = txtDBFolderPath.Text;
            if (!Directory.Exists(inputPath))
            {
                MessageBox.Show(this, "Input database folder not found.", "Directory not found", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            var defAppInst = new DatabaseClasses.Application();
            var files = Directory.GetFiles(inputPath, "*.json", SearchOption.AllDirectories);
            var db = new DatabaseClasses.AppDatabase();
            foreach (string fpath in files)
            {
                // Don't try to load output file
                if (fpath.Equals(outputPath, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                try
                {
                    var loadedAppInst = SerializationHelper.DeserializeFromFile(fpath, defAppInst);
                    if (string.IsNullOrEmpty(loadedAppInst.Name))
                    {
                        MessageBox.Show($"No app name provided in profile:\n{fpath}.\n\nProfile creation aborted.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    db.KnownApplications.Add(loadedAppInst);
                }
                catch
                {
                    MessageBox.Show($"Unloadable profile:\n{fpath}.\n\nProfile creation aborted.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            db.Save(outputPath);
            MessageBox.Show(this, "Creation of collections finished.", "Success.", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btnExit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void btnAssocOutputBrowse_Click(object sender, EventArgs e)
        {
            if (fbd.ShowDialog(this) == System.Windows.Forms.DialogResult.Cancel)
                return;

            txtAssocOutputPath.Text = fbd.SelectedPath;
        }

        private void btnUpdateInstallerBrowse_Click(object sender, EventArgs e)
        {
            ofd.Filter = "All files (*)|*";
            if (ofd.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
            {
                txtUpdateInstallerProjectDir.Text = ofd.FileName;
            }
        }

        private void btnUpdateOutputBrowse_Click(object sender, EventArgs e)
        {
            fbd.SelectedPath = txtUpdateOutput.Text;
            if (fbd.ShowDialog(this) == System.Windows.Forms.DialogResult.Cancel)
                return;

            txtUpdateOutput.Text = fbd.SelectedPath;
        }

        private void btnUpdateCreate_Click(object sender, EventArgs e)
        {
            const string DB_OUT_NAME = "database.def";
            const string HOSTS_OUT_NAME = "hosts.def";
            const string DESCRIPTOR_NAME = "update.json";
            const string DESCRIPTOR_TEMPLATE_NAME = "update_template.json";
            const string MSI_FILENAME_X86 = "PromptWall_x86.msi";
            const string MSI_FILENAME_ARM64 = "PromptWall_arm64.msi";

            string projectDir = txtUpdateInstallerProjectDir.Text;
            string msiX86Path = Path.Combine(projectDir, @"bin\Release\" + MSI_FILENAME_X86);
            string msiArm64Path = Path.Combine(projectDir, @"bin\Release\" + MSI_FILENAME_ARM64);
            string hostsPath = Path.Combine(projectDir, @"Sources\CommonAppData\PromptWall\hosts.bck");
            string profilesPath = Path.Combine(projectDir, @"Sources\CommonAppData\PromptWall\profiles.json");
            string twAssemblyPath = Path.Combine(projectDir, @"Sources\ProgramFiles\PromptWall\PromptWall.exe");

            UpdateModule prepare_module(string component_id, string src_filepath, string dst_filename, string version, bool compress)
            {
                if (!File.Exists(src_filepath))
                    throw new FileNotFoundException($"File\n\n{src_filepath}\n\nnot found.");

                string dst_filepath = Path.Combine(txtUpdateOutput.Text, dst_filename);
                if (compress)
                    Utils.CompressDeflate(src_filepath, dst_filepath);
                else
                    File.Copy(src_filepath, dst_filepath, true);

                return new UpdateModule
                {
                    Component = component_id,
                    ComponentVersion = version,
                    DownloadHash = Hasher.HashFile(src_filepath),
                    UpdateURL = txtUpdateURL.Text + dst_filename
                };
            }

            try
            {
                if (!File.Exists(twAssemblyPath))
                    throw new FileNotFoundException(string.Empty, twAssemblyPath);
                if (!Directory.Exists(txtUpdateOutput.Text))
                    throw new FileNotFoundException(string.Empty, txtUpdateOutput.Text);

                var version_info = FileVersionInfo.GetVersionInfo(twAssemblyPath).ProductVersion.ToString().Trim();
                var timestamp = DateTime.UtcNow.ToString("O");
                var update = new UpdateDescriptor
                {
                    Modules = new UpdateModule[4]
                    {
                        prepare_module("PromptWall_x86", msiX86Path, MSI_FILENAME_X86, version_info, false),
                        prepare_module("PromptWall_arm64", msiArm64Path, MSI_FILENAME_ARM64, version_info, false),
                        prepare_module("Database", profilesPath, DB_OUT_NAME, timestamp, true),
                        prepare_module("HostsFile", hostsPath, HOSTS_OUT_NAME, timestamp, true)
                    }
                };

                SerializationHelper.SerializeToFile(update, Path.Combine(txtUpdateOutput.Text, DESCRIPTOR_NAME));
                update.Modules[3].DownloadHash = "[HOSTS_SHA256_PLACEHOLDER]";
                SerializationHelper.SerializeToFile(update, Path.Combine(txtUpdateOutput.Text, DESCRIPTOR_TEMPLATE_NAME));
            }
            catch (FileNotFoundException ex)
            {
                MessageBox.Show(this, $"File or directory\n\n{ex?.FileName ?? "null"}\n\nnot found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MessageBox.Show(this, "Update created.", "Success.", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static int CountOccurence(string haystack, char needle)
        {
            int count = 0;
            foreach (char c in haystack)
                if (c == needle) count++;

            return count;
        }

        private void btnAddPrimaries_Click(object sender, EventArgs e)
        {
            ofd.Filter = "XML resources (*.resx)|*.resx|All files (*)|*";
            ofd.AutoUpgradeEnabled = true;
            ofd.Multiselect = true;
            if (ofd.ShowDialog(this) == System.Windows.Forms.DialogResult.Cancel)
                return;

            for (int i = 0; i < ofd.FileNames.Length; ++i)
            {
                string primary = ofd.FileNames[i];
                if (CountOccurence(Path.GetFileName(primary), '.') != 1)
                    continue;   // This is not a primary at all...

                string dir = Path.GetDirectoryName(primary);
                string primaryBase = Path.GetFileNameWithoutExtension(primary);
                string[] satellites = Directory.GetFiles(dir, primaryBase + ".*.resx", SearchOption.TopDirectoryOnly);
                ResXInputs.Add(new KeyValuePair<string, string[]>(primary, satellites));
            }

            listPrimaries.Items.Clear();
            for (int i = 0; i < ResXInputs.Count; ++i)
                listPrimaries.Items.Add(Path.GetFileName(ResXInputs[i].Key));
        }

        private void listPrimaries_SelectedIndexChanged(object sender, EventArgs e)
        {
            listSatellites.Items.Clear();
            if (listPrimaries.SelectedIndices.Count > 0)
            {
                KeyValuePair<string, string[]> pair = ResXInputs[listPrimaries.SelectedIndex];
                object[] sats = new object[pair.Value.Length];
                for (int i = 0; i < sats.Length; ++i)
                    sats[i] = Path.GetFileName(pair.Value[i]);
                listSatellites.Items.AddRange(sats);
            }
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            listPrimaries.Items.Clear();
            listSatellites.Items.Clear();
            ResXInputs.Clear();
        }

        private static Dictionary<string, ResXDataNode> ReadResXFile(string filePath)
        {
            var resxContents = new Dictionary<string, ResXDataNode>();
            using var resxReader = new ResXResourceReader(filePath);
            resxReader.UseResXDataNodes = true;
            IDictionaryEnumerator dict = resxReader.GetEnumerator();
            while (dict.MoveNext())
            {
                ResXDataNode node = (ResXDataNode)dict.Value;
                resxContents.Add(node.Name, node);
            }
            return resxContents;
        }

        private void btnOptimize_Click(object sender, EventArgs e)
        {
            ITypeResolutionService? trs = null;

            for (int i = 0; i < ResXInputs.Count; ++i)  // for each main resource file
            {
                var pair = ResXInputs[i];
                var primary = ReadResXFile(pair.Key);

                for (int s = 0; s < pair.Value.Length; ++s)  // for each localization
                {
                    { // Replace Windows Forms control versions to 4.0.0.0.
                        string primaryText;
                        using (var sr = new StreamReader(pair.Value[s], Encoding.UTF8))
                            primaryText = sr.ReadToEnd();

                        primaryText = primaryText.Replace(", Version=2.0.0.0,", ", Version=4.0.0.0,");

                        using var sw = new StreamWriter(pair.Value[s], false, Encoding.UTF8);
                        sw.Write(primaryText);
                    }

                    var satellite = ReadResXFile(pair.Value[s]);
                    var newSatellite = new Dictionary<string, ResXDataNode>();

                    // Iterate over all contents of primary.
                    // For each entry, check if one with same name, type and contents is available in
                    // satellite, and if so, don't save it to output.
                    var primaryEnum = primary.GetEnumerator();
                    while (primaryEnum.MoveNext())
                    {
                        ResXDataNode primaryItem = primaryEnum.Current.Value;
                        if (!satellite.ContainsKey(primaryItem.Name))
                            continue;

                        ResXDataNode satelliteItem = satellite[primaryItem.Name];

                        // We only allow specific properties to be localized
                        if (satelliteItem.Name.Contains("."))   // this is to prevent removing items from Messages.resx or Exceptions.resx
                        {
                            if (!satelliteItem.Name.EndsWith(".Text") &&
                                !satelliteItem.Name.EndsWith(".Title") &&
                                !satelliteItem.Name.EndsWith(".Filter") &&
                                !satelliteItem.Name.EndsWith(".AccessibleName"))
                                continue;
                        }

                        // We don't save values that are the same as default
                        if (satelliteItem.GetValue(trs).Equals(primaryItem.GetValue(trs)))
                            continue;

                        // Save resource item
                        newSatellite.Add(satelliteItem.Name, satelliteItem);
                    }

                    // Write output ResX file
                    string outPath = Path.Combine(txtOutputPath.Text, Path.GetFileName(pair.Value[s]));
                    using var resxWriter = new ResXResourceWriter(outPath);
                    Dictionary<string, ResXDataNode>.Enumerator outputEnum = newSatellite.GetEnumerator();
                    while (outputEnum.MoveNext())
                        resxWriter.AddResource(outputEnum.Current.Value);
                    resxWriter.Generate();
                } // for each localization
            } // for each primary
        }

        private void btnCertBrowse_Click(object sender, EventArgs e)
        {
            ofd.Filter = "All files (*)|*";
            if (File.Exists(txtCert.Text) || Directory.Exists(txtCert.Text))
            {
                ofd.InitialDirectory = Path.GetDirectoryName(txtCert.Text);
            }
            if (ofd.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
            {
                txtCert.Text = ofd.FileName;
            }
        }

        private void btnSignDir_Click(object sender, EventArgs e)
        {
            fbd.SelectedPath = txtSignDir.Text;
            if (fbd.ShowDialog(this) == System.Windows.Forms.DialogResult.Cancel)
                return;

            txtSignDir.Text = fbd.SelectedPath;
        }

        private void btnBatchSign_Click(object sender, EventArgs e)
        {
            if (!Directory.Exists(txtSignDir.Text))
            {
                MessageBox.Show(this, "Signing directory is invalid!");
                return;
            }
            if (!File.Exists(txtSigntool.Text))
            {
                MessageBox.Show(this, "Signtool.exe not found!");
                return;
            }

            btnBatchSign.Enabled = false;
            SignFiles(txtSignDir.Text, SIGNING_FILE_PATTERNS);
            btnBatchSign.Enabled = true;
        }

        private void SignFiles(string dirPath, string[] filePatterns)
        {
            // Collect all files to sign
            var filesToSign = new List<string>();
            foreach (var pattern in filePatterns)
            {
                string[] candidateFiles = Directory.GetFiles(dirPath, pattern, SearchOption.AllDirectories);
                foreach (var filePath in candidateFiles)
                {
                    var signedStatus = pylorak.Windows.WinTrust.VerifyFileAuthenticode(filePath);
                    if (signedStatus == Windows.WinTrust.VerifyResult.SIGNATURE_MISSING)
                    {
                        filesToSign.Add("\"" + filePath + "\"");
                    }
                    else if (signedStatus == Windows.WinTrust.VerifyResult.SIGNATURE_INVALID)
                    {
                        MessageBox.Show(this, string.Format("File \"{0}\" has pre-existing INVALID certificate. Signing will be aborted for all files.", filePath), "Signing result", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
            }

            if (filesToSign.Count == 0)
            {
                MessageBox.Show(this, "No files to sign, or all files are already signed.", "Signing result", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            // Assemble signtool command
            string signParams = string.Format("sign /d PromptWall /n \"{0}\" /tr \"{1}\" /td sha256 /fd sha256 /v {2}",
                    txtCert.Text,
                    txtTimestampingServ.Text,
                    string.Join(" ", filesToSign));

            // Execute signing process
            bool signSuccess;
            using (Process p = Utils.StartProcess(txtSigntool.Text, signParams, false))
            {
                p.WaitForExit();
                signSuccess = (p.ExitCode == 0);
            }
            if (signSuccess)
                MessageBox.Show(this, "Files successfully signed.", "Signing result", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this, "Failed to sign files.", "Signing result", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void btnSigntoolBrowse_Click(object sender, EventArgs e)
        {
            ofd.Filter = "Executables (*.exe)|*.exe|All files (*)|*";
            if (ofd.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
            {
                txtSigntool.Text = ofd.FileName;
            }

        }
    }
}
