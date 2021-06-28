using Newtonsoft.Json;
using NHB3.Types;
using System;
using System.IO;
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

		public void loadSettings()
		{
			String fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
			if (File.Exists(fileName))
			{
				BotSettings saved = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(@fileName));
				//this.checkBox1.Checked = saved.reffilOrder;
				//this.checkBox2.Checked = saved.lowerPrice;
				//this.checkBox3.Checked = saved.increasePrice;
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

			//if (this.checkBox1.Checked) current.reffilOrder = true;
			//if (this.checkBox2.Checked) current.lowerPrice = true;
			//if (this.checkBox3.Checked) current.increasePrice = true;
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
}