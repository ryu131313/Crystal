using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.MirForms.Systems
{
    public partial class MonsterTunerForm : Form
    {
        public Envir Envir => SMain.Envir;

        public MonsterTunerForm()
        {
            InitializeComponent();
            
            for (int i = 0; i < Envir.MonsterInfoList.Count; i++)
            {
                SelectMonsterComboBox.Items.Add(Envir.MonsterInfoList[i]);
            }
        }

        private void SelectMonsterComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            ComboBox comboBox = (ComboBox)sender;

            MonsterInfo monster = (MonsterInfo)comboBox.SelectedItem;

            if (monster == null) return;

            MonsterNameTextBox.Text = monster.Name;
            HPTextBox.Text = monster.Stats[Stat.HP].ToString();
            EffectTextBox.Text = monster.Effect.ToString();
            LevelTextBox.Text = monster.Level.ToString();
            ViewRangeTextBox.Text = monster.ViewRange.ToString();
            CoolEyeTextBox.Text = monster.CoolEye.ToString();
            MinACTextBox.Text = monster.Stats[Stat.MinAC].ToString();
            MaxACTextBox.Text = monster.Stats[Stat.MaxAC].ToString();
            MinMACTextBox.Text = monster.Stats[Stat.MinMAC].ToString();
            MaxMACTextBox.Text = monster.Stats[Stat.MaxMAC].ToString();
            MinDCTextBox.Text = monster.Stats[Stat.MinDC].ToString();
            MaxDCTextBox.Text = monster.Stats[Stat.MaxDC].ToString();
            MinMCTextBox.Text = monster.Stats[Stat.MinMC].ToString();
            MaxMCTextBox.Text = monster.Stats[Stat.MaxMC].ToString();
            MinSCTextBox.Text = monster.Stats[Stat.MinSC].ToString();
            MaxSCTextBox.Text = monster.Stats[Stat.MaxSC].ToString();
            AccuracyTextBox.Text = monster.Stats[Stat.Accuracy].ToString();
            AgilityTextBox.Text = monster.Stats[Stat.Agility].ToString();
            ASpeedTextBox.Text = monster.AttackSpeed.ToString();
            MSpeedTextBox.Text = monster.MoveSpeed.ToString();
        }

        private void updateButton_Click(object sender, EventArgs e)
        {
            MonsterInfo monster = (MonsterInfo)SelectMonsterComboBox.SelectedItem;

            if (monster == null) return;

            try
            {
                monster.Stats[Stat.HP] = int.Parse(HPTextBox.Text);
                monster.Effect = byte.Parse(EffectTextBox.Text);
                monster.Level = ushort.Parse(LevelTextBox.Text);
                monster.ViewRange = byte.Parse(ViewRangeTextBox.Text);
                monster.CoolEye = byte.Parse(CoolEyeTextBox.Text);
                monster.Stats[Stat.MinAC] = ushort.Parse(MinACTextBox.Text);
                monster.Stats[Stat.MaxAC] = ushort.Parse(MaxACTextBox.Text);
                monster.Stats[Stat.MinMAC] = ushort.Parse(MinMACTextBox.Text);
                monster.Stats[Stat.MaxMAC] = ushort.Parse(MaxMACTextBox.Text);
                monster.Stats[Stat.MinDC] = ushort.Parse(MinDCTextBox.Text);
                monster.Stats[Stat.MaxDC] = ushort.Parse(MaxDCTextBox.Text);
                monster.Stats[Stat.MinMC] = ushort.Parse(MinMCTextBox.Text);
                monster.Stats[Stat.MaxMC] = ushort.Parse(MaxMCTextBox.Text);
                monster.Stats[Stat.MinSC] = ushort.Parse(MinSCTextBox.Text);
                monster.Stats[Stat.MaxSC] = ushort.Parse(MaxSCTextBox.Text);
                monster.Stats[Stat.Accuracy] = byte.Parse(AccuracyTextBox.Text);
                monster.Stats[Stat.Agility] = byte.Parse(AgilityTextBox.Text);
                monster.AttackSpeed = ushort.Parse(ASpeedTextBox.Text);
                monster.MoveSpeed = ushort.Parse(MSpeedTextBox.Text);
            }
            catch
            {
                MessageBox.Show("Value validation failed. Please correct before updating", "Notice",
                MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                return;
            }

            foreach (var item in Envir.Objects)
            {
                if (item.Race != ObjectType.Monster) continue;

                MonsterObject mob = (MonsterObject)item;

                mob.RefreshAll();
            }
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < SelectMonsterComboBox.Items.Count; i++)
            {
                MonsterInfo mob = (MonsterInfo)SelectMonsterComboBox.Items[i];

                if (mob == null) continue;

                if (Envir.MonsterInfoList[i].Index != mob.Index) break;

                Envir.MonsterInfoList[i] = mob;
            }

            // Approved Known-debt fix (2026-10-10): Main.SaveDB() would overwrite newer data saved through the editor copy (LiveDb saves
            // on every web apply; SystemDataStale after any editor save) and races the game thread's own save.
            if (Settings.LiveDbEnabled || Envir.SystemDataStale)
            {
                MessageBox.Show("The tuned values were applied to the running world only and were NOT saved to Server.MirDB: " +
                                (Settings.LiveDbEnabled ? "LiveDb is enabled, so Server.MirDB is written only through the web admin." : "newer game data was saved through the editor since the world loaded.") +
                                " Make the same change in the web admin (Game Data → Monsters) to keep it after a restart.",
                    "Monster Tuner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Envir.SaveDB();
        }
    }
}
