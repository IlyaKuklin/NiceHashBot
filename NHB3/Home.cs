using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NHB3.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Threading.Timer;

namespace NHB3
{
	public partial class Home : Form
	{
		private readonly ApiConnect _ac;
		private readonly string _currency = "TBTC";

		private JArray _orders;
		private JArray _market;
		private readonly Timer _timer;
		private readonly BotSettings _botSettings;
		private readonly string _botId;

		private readonly Processor _processor;

		public Home()
		{
			InitializeComponent();

			var ticks = new DateTime(2016, 1, 1).Ticks;
			var ans = DateTime.Now.Ticks - ticks;
			_botId = ans.ToString("x");
			idLabel.Text = _botId;

			_ac = new ApiConnect();

			var acSettings = _ac.readSettings();

			if (acSettings.OrganizationID != null)
			{
				_ac.setup(acSettings);

				//ClearOrders();

				if (acSettings.Enviorment == 1)
					_currency = "BTC";
				_ac.currency = _currency;

				refreshOrders(false);
				_ac.getPools(true);

				var fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
				if (!File.Exists(fileName))
					return;

				_botSettings = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(fileName));
				_processor = new Processor(_ac, _botSettings, _orders, _botId);

				_timer = new Timer(
					e =>
					{
						try
						{
							toolStripStatusLabel1.Text = "Working";
							CheckForIllegalCrossThreadCalls = false;
							_processor.RunBot();
							toolStripStatusLabel1.Text = "Idle";
						}
						catch (Exception ex)
						{
							toolStripStatusLabel1.Text = "Error";
							HandleException(ex);
						}
					},
					null,
					TimeSpan.Zero,
					TimeSpan.FromSeconds(_botSettings.RunBotDelay));
			}
		}

		private void api_Click(object sender, EventArgs e)
		{
			ApiForm af = new ApiForm(_ac);
			af.FormBorderStyle = FormBorderStyle.FixedSingle;
		}

		private void pools_Click(object sender, EventArgs e)
		{
			PoolsForm pf = new PoolsForm(_ac);
			pf.FormBorderStyle = FormBorderStyle.FixedSingle;
		}

		private void botToolStripMenuItem_Click(object sender, EventArgs e)
		{
			BotForm bf = new BotForm();
			bf.FormBorderStyle = FormBorderStyle.FixedSingle;
		}

		private void newOrderToolStripMenuItem_Click(object sender, EventArgs e)
		{
			OrderForm of = new OrderForm(_ac);
			of.FormBorderStyle = FormBorderStyle.FixedSingle;
			of.FormClosed += new FormClosedEventHandler(f_FormClosed); //refresh orders
		}

		private void ordersToolStripMenuItem_Click(object sender, EventArgs e)
		{
			refreshOrders(false);
		}

		//private void balanceToolStripMenuItem_Click(object sender, EventArgs e)
		//{
		//	refreshBalance();
		//}


		private void refreshOrders(bool fromThread)
		{
			if (_ac.connected)
			{
				_orders = _ac.getOrders();

				//filter out data
				JArray cleanOrders = new JArray();
				foreach (JObject order in _orders)
				{
					JObject cleanOrder = new JObject();
					cleanOrder.Add("id", "" + order["id"]);
					cleanOrder.Add("market", "" + order["market"]);
					cleanOrder.Add("pool", "" + order["pool"]["name"]);
					cleanOrder.Add("type", ("" + order["type"]["code"]).Equals("STANDARD") ? "standard" : "fixed");
					cleanOrder.Add("algorithm", "" + order["algorithm"]["algorithm"]);
					cleanOrder.Add("amount", "" + order["amount"]);
					cleanOrder.Add("payedAmount", "" + order["payedAmount"]);
					cleanOrder.Add("availableAmount", "" + order["availableAmount"]);

					float payed = float.Parse("" + order["payedAmount"], CultureInfo.InvariantCulture);
					float available = float.Parse("" + order["availableAmount"], CultureInfo.InvariantCulture);
					float spent_factor = payed / available * 100;

					cleanOrder.Add("spentPercent", "" + spent_factor.ToString("0.00") + "%");
					cleanOrder.Add("limit", "" + order["limit"]);
					cleanOrder.Add("price", "" + order["price"]);

					cleanOrder.Add("rigsCount", "" + order["rigsCount"]);
					cleanOrder.Add("acceptedCurrentSpeed", "" + order["acceptedCurrentSpeed"]);
					cleanOrders.Add(cleanOrder);
				}

				if (fromThread)
				{
					dataGridView1.Invoke((MethodInvoker)delegate
					{
						dataGridView1.DataSource = cleanOrders;
					});
				}
				else
				{
					dataGridView1.DataSource = cleanOrders;
				}

				dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
				dataGridView1.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
				dataGridView1.AllowUserToOrderColumns = true;
				dataGridView1.AllowUserToResizeColumns = true;
			}
		}

		private void refreshMarket()
		{
			if (_ac.connected)
			{
				_market = _ac.getMarket();
			}
		}

		private void autoPilotOffToolStripMenuItem_Click(object sender, EventArgs e)
		{
			toolStripStatusLabel1.Text = "Stopped";
			_processor.SwitchState(false);
		}

		private void autoPilotONToolStripMenuItem_Click(object sender, EventArgs e)
		{
			toolStripStatusLabel1.Text = "Idle";
			_processor.SwitchState(true);
			try
			{
				_processor.RunBot();
			}
			catch (Exception ex)
			{
				HandleException(ex);
			}
		}

		private void editSelectedOrderToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (dataGridView1.Rows.GetRowCount(DataGridViewElementStates.Selected) == 1)
			{
				OrderForm of = new OrderForm(_ac);
				of.FormBorderStyle = FormBorderStyle.FixedSingle;
				of.setEditMode((JObject)_orders[dataGridView1.SelectedRows[0].Index]);
				of.FormClosed += new FormClosedEventHandler(f_FormClosed); //refresh orders
			}
		}

		private void f_FormClosed(object sender, FormClosedEventArgs e)
		{
			refreshOrders(false);
		}

		private long lastRunStamp;

		private Dictionary<string, float> getOrderPriceRangesForAlgoAndMarket(string oa, string om)
		{
			var prices = new Dictionary<string, float>();

			foreach (JObject order in _market)
			{
				string order_type = "" + order["type"];
				string order_algo = "" + order["algorithm"];
				string order_market = "" + order["market"];
				float order_speed = float.Parse("" + order["acceptedCurrentSpeed"], CultureInfo.InvariantCulture);
				string order_price = "" + order["price"];

				if (order_type.Equals("STANDARD") && order_algo.Equals(oa) && order_market.Equals(om) && order_speed > 0)
				{
					if (prices.ContainsKey(order_price))
					{
						prices[order_price] = prices[order_price] + order_speed;
					}
					else
					{
						prices[order_price] = order_speed;
					}
				}
			}
			return prices;
		}

		private void ClearOrders()
		{
			var orders = _ac.getOrders();
			foreach (var order in orders)
			{
				_ac.cancelOrder(order["id"].ToString());
			}
		}

		private void HandleException(Exception ex)
		{
			var message = $"Ошибка в боте с ID ({_botId}).\n\nТекст ошибки:\n\n{ex}\n\nОжидание {_botSettings.ErrorDelay} секунд перед перезапуском";

			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(message);

			var urlString = $"https://api.telegram.org/bot{_botSettings.TgBotToken}/sendMessage?chat_id={_botSettings.TgChatId}&text={message}";
			var webclient = new WebClient();
			webclient.DownloadString(urlString);

			if (!string.IsNullOrEmpty(_botSettings.ErrorUrlHandler))
			{
				var request = WebRequest.Create(_botSettings.ErrorUrlHandler);
				var response = request.GetResponse();
				response.Dispose();
			}

			//_isErrorState = true;
			_processor.SwitchErrorState(true);
			_processor.SwicthCycle(false);
			Thread.Sleep(TimeSpan.FromSeconds(_botSettings.ErrorDelay));
			_processor.SwitchErrorState(false);
			//_isErrorState = false;

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("Перезапуск бота");
		}
	}
}