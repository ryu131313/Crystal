using Server.MirEnvir.Storage;
using Server.MirDatabase;
using Server.MirEnvir;
using System.Diagnostics;

namespace Server
{
    public partial class QuestInfoForm : Form
    {
        public string QuestListPath = Path.Combine(Settings.ExportPath, "QuestList.csv");

        public Envir Envir => SMain.EditEnvir;

        private List<QuestInfo> _selectedQuestInfos;

        public QuestInfoForm()
        {
            InitializeComponent();

            QTypeComboBox.Items.AddRange(Enum.GetValues(typeof(QuestType)).Cast<object>().ToArray());
            RequiredClassComboBox.Items.AddRange(Enum.GetValues(typeof(RequiredClass)).Cast<object>().ToArray());
            
            QuestInfoListBox.SelectionMode = SelectionMode.One;
            QuestSearchBox_TextChanged(this, EventArgs.Empty);

            UpdateInterface();

            // Quest system V2 (UserData-and-Quests-Plan.md, Part B phase 1): quest ids are stable and never reused (the V2 quest tables,
            // their LegacySource and later the quest history key on them), and Remove reset QuestIndex to 0 when the list emptied. Quests
            // are added and removed in the web admin only.
            AddButton.Enabled = false;
            RemoveButton.Enabled = false;
        }

        private void AddButton_Click(object sender, EventArgs e) => ShowAddRemoveDisabled();

        private void RemoveButton_Click(object sender, EventArgs e) => ShowAddRemoveDisabled();

        private static void ShowAddRemoveDisabled() =>
            MessageBox.Show("Adding and removing quests here is disabled: use the web admin (quest ids must never be reused).", "Quests");

        private void UpdateInterface()
        {
            // Get selected quests from the ListBox
            _selectedQuestInfos = QuestInfoListBox.SelectedItems.Cast<QuestInfo>().ToList();

            if (_selectedQuestInfos.Count == 0)
            {
                ClearInterface();
                return;
            }

            QuestInfo info = _selectedQuestInfos[0];

            // Enable the panel and populate fields with selected quest details
            QuestInfoPanel.Enabled = true;
            QuestIndexTextBox.Text = info.Index.ToString();
            QFileNameTextBox.Text = info.FileName;
            QNameTextBox.Text = info.Name;
            QGroupTextBox.Text = info.Group;
            QTypeComboBox.SelectedItem = info.Type;

            QGotoTextBox.Text = info.GotoMessage;
            QKillTextBox.Text = info.KillMessage;
            QItemTextBox.Text = info.ItemMessage;
            QFlagTextBox.Text = info.FlagMessage;

            RequiredMinLevelTextBox.Text = info.RequiredMinLevel.ToString();
            RequiredMaxLevelTextBox.Text = info.RequiredMaxLevel.ToString();

            if (Convert.ToInt32(RequiredMaxLevelTextBox.Text) <= 0)
                RequiredMaxLevelTextBox.Text = byte.MaxValue.ToString();

            QuestInfo tempQuest = Envir.QuestInfoList.FirstOrDefault(c => c.Index == info.RequiredQuest);

            RequiredQuestComboBox.Items.Clear();
            RequiredQuestComboBox.Items.Add(new QuestInfo { Index = 0, Name = "None" });
            RequiredQuestComboBox.Items.AddRange(Envir.QuestInfoList.ToArray());
            RequiredQuestComboBox.SelectedItem = tempQuest ?? RequiredQuestComboBox.Items[0];
            RequiredClassComboBox.SelectedItem = info.RequiredClass;

            TimeLimitTextBox.Text = info.TimeLimitInSeconds.ToString();

            for (int i = 1; i < _selectedQuestInfos.Count; i++)
            {
                info = _selectedQuestInfos[i];

                if (QFileNameTextBox.Text != info.FileName) QFileNameTextBox.Text = string.Empty;
                if (QNameTextBox.Text != info.Name) QNameTextBox.Text = string.Empty;
                if (QGroupTextBox.Text != info.Group) QGroupTextBox.Text = string.Empty;

                if (QTypeComboBox.SelectedItem != null &&
                    (QuestType)QTypeComboBox.SelectedItem != info.Type)
                {
                    QTypeComboBox.SelectedItem = null;
                }

                if (QGotoTextBox.Text != info.GotoMessage) QGotoTextBox.Text = string.Empty;
                if (QKillTextBox.Text != info.KillMessage) QKillTextBox.Text = string.Empty;
                if (QItemTextBox.Text != info.ItemMessage) QItemTextBox.Text = string.Empty;

                // BUG 1: compare against FlagMessage (not ItemMessage)
                if (QFlagTextBox.Text != info.FlagMessage) QFlagTextBox.Text = string.Empty;

                if (RequiredMinLevelTextBox.Text != info.RequiredMinLevel.ToString())
                    RequiredMinLevelTextBox.Text = string.Empty;

                if (RequiredMaxLevelTextBox.Text != info.RequiredMaxLevel.ToString())
                    RequiredMaxLevelTextBox.Text = byte.MaxValue.ToString();

                // BUG 2: items are QuestInfo objects; compare selected item's Index
                if (RequiredQuestComboBox.SelectedItem is QuestInfo selReq &&
                    selReq.Index != info.RequiredQuest)
                {
                    RequiredQuestComboBox.SelectedItem = null;
                }

                if (RequiredClassComboBox.SelectedItem != null &&
                    (RequiredClass)RequiredClassComboBox.SelectedItem != info.RequiredClass)
                {
                    RequiredClassComboBox.SelectedItem = null;
                }

                // BUG 3: use Text, not SelectedText, and don't force "0" unless mismatched
                if (TimeLimitTextBox.Text != info.TimeLimitInSeconds.ToString())
                    TimeLimitTextBox.Text = "0";
            }
        }

        // Method to clear the interface
        private void ClearInterface()
        {
            QuestInfoPanel.Enabled = false;
            QuestIndexTextBox.Text = string.Empty;
            QFileNameTextBox.Text = string.Empty;
            QNameTextBox.Text = string.Empty;
            QGroupTextBox.Text = string.Empty;
            QTypeComboBox.SelectedItem = null;

            QGotoTextBox.Text = string.Empty;
            QKillTextBox.Text = string.Empty;
            QItemTextBox.Text = string.Empty;
            QFlagTextBox.Text = string.Empty;

            RequiredMinLevelTextBox.Text = string.Empty;
            RequiredMaxLevelTextBox.Text = string.Empty;
            RequiredQuestComboBox.SelectedItem = null;
            RequiredClassComboBox.SelectedItem = null;

            TimeLimitTextBox.Text = string.Empty;
        }

        private void RefreshQuestList(bool preserveSelection = true)
        {
            QuestInfoListBox.SelectedIndexChanged -= QuestInfoListBox_SelectedIndexChanged;

            // Capture selection indices only if preserving
            List<int> selectedIndices = preserveSelection
                ? QuestInfoListBox.SelectedIndices.Cast<int>().ToList()
                : new List<int>();

            QuestInfoListBox.BeginUpdate();

            QuestInfoListBox.Items.Clear();
            // Fast add
            QuestInfoListBox.Items.AddRange(Envir.QuestInfoList.ToArray());

            // Clear selection, then optionally restore
            QuestInfoListBox.ClearSelected();
            if (preserveSelection)
            {
                foreach (int i in selectedIndices)
                    if (i >= 0 && i < QuestInfoListBox.Items.Count)
                        QuestInfoListBox.SetSelected(i, true);
            }

            QuestInfoListBox.EndUpdate();

            QuestInfoListBox.SelectedIndexChanged += QuestInfoListBox_SelectedIndexChanged;
        }

        private void QuestInfoListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateInterface();
        }

        private void QuestInfoForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            Envir.SaveDB();
        }

        private void PasteMButton_Click(object sender, EventArgs e)
        {
            string data = Clipboard.GetText();

            if (!data.StartsWith("Quest", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Cannot Paste, Copied data is not Quest Information.");
                return;
            }


            string[] npcs = data.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);


            //for (int i = 1; i < npcs.Length; i++)
            //    NPCInfo.FromText(npcs[i]);
            RefreshQuestList();
            UpdateInterface();
        }


        private void ExportAllButton_Click(object sender, EventArgs e)
        {
            ExportQuests(Envir.QuestInfoList);
        }

        private void ExportSelected_Click(object sender, EventArgs e)
        {
            var list = QuestInfoListBox.SelectedItems.Cast<QuestInfo>().ToList();

            ExportQuests(list);
        }

        public void ExportQuests(List<QuestInfo> Quests)
        {
            if (Quests.Count == 0) return;

            SaveFileDialog sfd = new SaveFileDialog();
            sfd.InitialDirectory = Path.Combine(Application.StartupPath, "Exports");
            sfd.FileName = "QuestInfoExport.csv";
            sfd.Filter = "CSV File|*.csv";
            sfd.ShowDialog();

            if (sfd.FileName == string.Empty) return;

            using (StreamWriter sw = File.CreateText(sfd.FileNames[0]))
            {
                sw.WriteLine("Index,Name,Group,Type,FileName,GotoMessage,KillMessage,ItemMessage,FlagMessage,RequiredMinLevel,RequiredMaxLevel,RequiredQuest,RequiredClass");
                for (int j = 0; j < Quests.Count; j++)
                {
                    sw.WriteLine(Quests[j].ToText());
                }
            }
            MessageBox.Show("Quest Export complete");
        }


        private void ImportButton_Click(object sender, EventArgs e)
        {
            string Path = string.Empty;

            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "CSV File|*.csv";
            ofd.ShowDialog();

            if (ofd.FileName == string.Empty) return;

            Path = ofd.FileName;

            foreach (var m in DesignFiles.ReadAllLines(Path).Skip(1))
                QuestInfo.FromText(m);

            RefreshQuestList();
            UpdateInterface();
            MessageBox.Show("Quest Import complete");
        }

        private void QNameTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].Name = ActiveControl.Text;

            RefreshQuestList();
        }

        private void QGroupTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].Group = ActiveControl.Text;
        }

        private void QTypeComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].Type = (QuestType)QTypeComboBox.SelectedItem;
        }

        private void QFileNameTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].FileName = ActiveControl.Text;

        }

        private void QGotoTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].GotoMessage = ActiveControl.Text;

        }

        private void QKillTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].KillMessage = ActiveControl.Text;

        }

        private void QItemTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].ItemMessage = ActiveControl.Text;
        }

        private void QFlagTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].FlagMessage = ActiveControl.Text;
        }


        private void RequiredMinLevelTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            int temp;

            if (!int.TryParse(ActiveControl.Text, out temp) || temp > Convert.ToInt32(RequiredMaxLevelTextBox.Text) || temp < byte.MinValue)
            {
                ActiveControl.BackColor = Color.Red;
                return;
            }
            ActiveControl.BackColor = SystemColors.Window;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].RequiredMinLevel = temp;
        }

        private void RequiredMaxLevelTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            int temp;

            if (!int.TryParse(ActiveControl.Text, out temp) || temp < Convert.ToInt32(RequiredMinLevelTextBox.Text) || temp > byte.MaxValue)
            {
                ActiveControl.BackColor = Color.Red;
                return;
            }
            ActiveControl.BackColor = SystemColors.Window;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].RequiredMaxLevel = temp;
        }

        private void RequiredQuestComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
            {
                QuestInfo temp = (QuestInfo)RequiredQuestComboBox.SelectedItem;

                _selectedQuestInfos[i].RequiredQuest = temp.Index;
            }
        }

        private void RequiredClassComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].RequiredClass = (RequiredClass)RequiredClassComboBox.SelectedItem;
        }

        private void OpenQButton_Click(object sender, EventArgs e)
        {
            if (QFileNameTextBox.Text == string.Empty) return;

            var scriptPath = Path.Combine(Settings.QuestPath, QFileNameTextBox.Text + ".txt");

            if (DesignFiles.Exists(scriptPath))
            {
                DesignEditing.OpenInEditor(scriptPath); // a store file opens as a working copy whose saves go back (roadmap P7)
            }
            else
            {
                if (!DesignFiles.TryMap(scriptPath, out _, out _))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                    File.Create(scriptPath).Close();
                }

                DesignFiles.WriteAllText(scriptPath,
                    $"{"[@Description]"}\r\n\r\n{"[@TaskDescription]"}\r\n\r\n{"[@Completion]"}\r\n\r\n{"[@KillTasks]"}\r\n\r\n{"[@ItemTasks]"}\r\n\r\n{"[@FlagTasks]"}\r\n\r\n{"[@CarryItems]"}\r\n\r\n{"[@FixedRewards]"}\r\n\r\n{"[@SelectRewards]"}\r\n\r\n{"[@ExpReward]"}\r\n\r\n{"[@GoldReward]"}");

                DesignEditing.OpenInEditor(scriptPath);
            }
        }

        private void TimeLimitTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ActiveControl != sender) return;

            int temp;

            if (!int.TryParse(ActiveControl.Text, out temp))
            {
                ActiveControl.BackColor = Color.Red;
                return;
            }
            ActiveControl.BackColor = SystemColors.Window;

            for (int i = 0; i < _selectedQuestInfos.Count; i++)
                _selectedQuestInfos[i].TimeLimitInSeconds = temp;
        }

        #region Quest Search
        private void QuestSearchBox_TextChanged(object sender, EventArgs e)
        {
            string searchText = QuestSearchBox.Text.Trim().ToLower();

            // Show all items if the search box is empty or contains only whitespace
            if (string.IsNullOrWhiteSpace(searchText))
            {
                RefreshQuestList();
                QuestInfoListBox.ClearSelected();
                return;
            }

            QuestInfoListBox.Items.Clear();

            // Filter quests based on search text
            foreach (var quest in Envir.QuestInfoList)
            {
                if (!string.IsNullOrEmpty(quest.Name) && quest.Name.ToLower().Contains(searchText) ||
                    !string.IsNullOrEmpty(quest.FileName) && quest.FileName.ToLower().Contains(searchText))
                {
                    QuestInfoListBox.Items.Add(quest);
                }
            }
        }
        #endregion
    }
}
