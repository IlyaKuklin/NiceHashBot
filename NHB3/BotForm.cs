using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NHB3
{
    public partial class BotForm : Form
    {

        //public bool refill;
        //public bool lower;
        //public bool increase;
        //public float limitIncrease;

        public BotForm()
        {
            InitializeComponent();
            this.Show();
            loadSettings();
        }

        public void loadSettings() {
            String fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
            if (File.Exists(fileName))
            {
                BotSettings saved = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(@fileName));
                this.checkBox1.Checked = saved.reffilOrder;
                this.checkBox2.Checked = saved.lowerPrice;
                this.checkBox3.Checked = saved.increasePrice;
                //this.newLimitTxtBox.Text = saved.maxLimitSpeed.ToString(new CultureInfo("en-US"));
                this.jsonSettingsTextBox.Text = saved.JsonSettingsUrl;
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            String fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
            BotSettings current = formSettings();
            File.WriteAllText(fileName, JsonConvert.SerializeObject(current));
            this.Close();
        }

        private BotSettings formSettings()
        {
            BotSettings current = new BotSettings();

            //current.reffilOrder = false;
            //current.lowerPrice = false;
            //current.increasePrice = false;

            if (this.checkBox1.Checked) current.reffilOrder = true;
            if (this.checkBox2.Checked) current.lowerPrice = true;
            if (this.checkBox3.Checked) current.increasePrice = true;
            //if (!string.IsNullOrEmpty(this.newLimitTxtBox.Text)) current.maxLimitSpeed = (float)Math.Round(Convert.ToDouble(this.newLimitTxtBox.Text, new CultureInfo("en-US")), 8);
            current.JsonSettingsUrl = this.jsonSettingsTextBox.Text;

            return current;
        }

		private void newLimitTxtBox_KeyPress(object sender, KeyPressEventArgs e)
		{
            // allows 0-9, backspace, and decimal
            if (((e.KeyChar < 48 || e.KeyChar > 57) && e.KeyChar != 8 && e.KeyChar != 46))
            {
                e.Handled = true;
                return;
            }
        }
	}

	public class BotSettings
    {
        public bool reffilOrder { get; set; }

        public bool lowerPrice { get; set; }

        public bool increasePrice { get; set; }

        /** Актуальные. */

        // Частота запуска логики бота (в секундах).
        public int RunBotDelay { get; set; }

        // url настроек цены.
        public string JsonSettingsUrl { get; set; }

        public int MinStepsCountToFindOrder { get; set; }

        public int RunRefillDelay { get; set; }

        // После какого остатка делать refill.
        public float RefillOrderLimit { get; set; }

        // Объём refill.
        public float RefillOrderAmount { get; set; }

        public string TgBotToken { get; set; }

        public string TgChatId { get; set; }

        public int ErrorDelay { get; set; }

		public float JsonPrice { get; set; }

        public List<BotMarketSettings> MarketSettings { get; set; }
    }

    public class BotMarketSettings
	{
		public string Name { get; set; }

        // Какую скорость устанавливать при перебитии ордера.
        public float MaxLimitSpeed { get; set; }
	}
}
