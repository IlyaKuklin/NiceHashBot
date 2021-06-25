using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NHB3.Types;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using static NHB3.ApiConnect;

namespace NHB3
{
	public partial class Home : Form
	{
		private ApiConnect ac;
		private string currency = "TBTC";
		private bool botRunning = false;
		private JArray orders;
		private JArray market;
		private List<string> marketNames;

		private System.Threading.Timer timer;

		private readonly BotSettings botSettings;

		private readonly string botId;

		public Home()
		{
			InitializeComponent();
			
			var ticks = new DateTime(2016, 1, 1).Ticks;
			var ans = DateTime.Now.Ticks - ticks;
			botId = ans.ToString("x");
			idLabel.Text = botId;

			ac = new ApiConnect();

			ApiSettings saved = ac.readSettings();

			if (saved.OrganizationID != null)
			{
				ac.setup(saved);

				//clearOrders();

				if (saved.Enviorment == 1)
				{
					currency = "BTC";
				}
				ac.currency = currency;
				refreshBalance();
				refreshOrders(false);
				ac.getPools(true);

				marketNames = ac.getMarkets();

				String fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
				if (!File.Exists(fileName))
					return;
				botSettings = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(fileName));

				timer = new System.Threading.Timer(
					e => {
						try
						{
							runBot();
						}
						catch (Exception ex)
						{
							HandleException(ex);
						}
					},
					null,
					TimeSpan.Zero,
					TimeSpan.FromSeconds(botSettings.runBotDelay));
			}
		}

		public Dictionary<string, float> TotalSpeedByMarket { get; set; } = new Dictionary<string, float>();
		public Dictionary<string, float> MinLimitByAlgoritm { get; set; } = new Dictionary<string, float>();
		public Dictionary<string, float> DownStepByAlgoritm { get; set; } = new Dictionary<string, float>();

		private void api_Click(object sender, EventArgs e)
		{
			ApiForm af = new ApiForm(ac);
			af.FormBorderStyle = FormBorderStyle.FixedSingle;
		}

		private void pools_Click(object sender, EventArgs e)
		{
			PoolsForm pf = new PoolsForm(ac);
			pf.FormBorderStyle = FormBorderStyle.FixedSingle;
		}

		private void botToolStripMenuItem_Click(object sender, EventArgs e)
		{
			BotForm bf = new BotForm();
			bf.FormBorderStyle = FormBorderStyle.FixedSingle;
		}

		private void newOrderToolStripMenuItem_Click(object sender, EventArgs e)
		{
			OrderForm of = new OrderForm(ac);
			of.FormBorderStyle = FormBorderStyle.FixedSingle;
			of.FormClosed += new FormClosedEventHandler(f_FormClosed); //refresh orders
		}

		private void ordersToolStripMenuItem_Click(object sender, EventArgs e)
		{
			refreshOrders(false);
		}

		private void balanceToolStripMenuItem_Click(object sender, EventArgs e)
		{
			refreshBalance();
		}

		private void refreshBalance()
		{
			if (ac.connected)
			{
				JObject balance = ac.getBalance(currency);
				if (balance != null)
				{
					this.toolStripStatusLabel2.Text = "Balance: " + balance["available"] + " " + currency;
				}
			}
			else
			{
				this.toolStripStatusLabel2.Text = "Balance: N/A " + currency;
			}
		}

		private void refreshOrders(bool fromThread)
		{
			if (ac.connected)
			{
				orders = ac.getOrders();

				//filter out data
				JArray cleanOrders = new JArray();
				foreach (JObject order in orders)
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
			if (ac.connected)
			{
				market = ac.getMarket();
			}
		}

		private void autoPilotOffToolStripMenuItem_Click(object sender, EventArgs e)
		{
			toolStripStatusLabel1.Text = "Stopped";
			botRunning = false;
		}

		private void autoPilotONToolStripMenuItem_Click(object sender, EventArgs e)
		{
			toolStripStatusLabel1.Text = "Idle";
			botRunning = true;
			runBot();
		}

		private void editSelectedOrderToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (dataGridView1.Rows.GetRowCount(DataGridViewElementStates.Selected) == 1)
			{
				OrderForm of = new OrderForm(ac);
				of.FormBorderStyle = FormBorderStyle.FixedSingle;
				of.setEditMode((JObject)orders[dataGridView1.SelectedRows[0].Index]);
				of.FormClosed += new FormClosedEventHandler(f_FormClosed); //refresh orders
			}
		}

		private void f_FormClosed(object sender, FormClosedEventArgs e)
		{
			refreshOrders(false);
		}

		private long delay = 20;
		private long lastRunStamp;

		private void runBot()
		{
			throw new Exception("test");

			// START
			if (!botRunning)
				return;

			long currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();

			if ((currentTimeStamp - lastRunStamp) >= delay)
			{
				Console.WriteLine("cycle");
				lastRunStamp = currentTimeStamp;
			}
			Console.WriteLine(currentTimeStamp);

			toolStripStatusLabel1.Text = "Working";

			Control.CheckForIllegalCrossThreadCalls = false;

			refreshOrders(true);
			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("orders to process: {0}", orders.Count);

			JArray jAlgorithms = ac.algorithms;

			var myOrders = getMyOrders();

			foreach (var myOrder in myOrders)
			{
				var t1 = myOrder.PayedAmount;
				var t2 = myOrder.AvailableAmount;

				ac.refillOrder(myOrder.Id, "0.001");
			}

			return;

			var myAlgorithmNames = myOrders.Select(x => x.AlgorithmName).Distinct().ToList();
			var myMarketNames = myOrders.Select(x => x.MarketName).Distinct().ToList();
			var myOrderIds = myOrders.Select(x => x.Id).ToList();
			var allBookOrders = getAllOrders(marketNames, jAlgorithms, myAlgorithmNames, myMarketNames).Where(x => !myOrderIds.Contains(x.Id)).ToList();
			var jsonPrice = getJsonPrice(botSettings.jsonSettingsUrl);

			var processedCombinations = new List<string>();

			bool needRaise = true;
			float targetPrice = 0.0f;
			foreach (var myOrder in myOrders)
			{
				var combinationKey = $"{myOrder.AlgorithmName}.{myOrder.MarketName}";
				var minLimit = this.MinLimitByAlgoritm[myOrder.AlgorithmName];

				if (myOrder.Price > jsonPrice && myOrder.Limit > minLimit)
				{
					slowDownOrder(myOrder);
				}
				else
				{
					if (processedCombinations.Contains(combinationKey) || !needRaise) continue;

					var targetOrders = allBookOrders
						.Where(x => x.MarketName == myOrder.MarketName && x.AlgorithmName == myOrder.AlgorithmName && x.Price <= jsonPrice)
						.OrderByDescending(x => x.Price)
						.ToList();

					var totalLimit = this.TotalSpeedByMarket[$"{myOrder.AlgorithmName}.{myOrder.MarketName}"];
					var currentLimit = 0.0f;

					foreach (var targetOrder in targetOrders)
					{
						currentLimit += targetOrder.Limit;
						if (targetOrder.Limit == 0 || currentLimit >= totalLimit)
						{
							Console.ForegroundColor = ConsoleColor.Yellow;
							Console.WriteLine($"Цена конкурирующего ордера: {targetOrder.Price}");

							targetPrice = (float)Math.Round(targetOrder.Price + 0.0001f, 4);
							var existingCorrectOrder = myOrders.FirstOrDefault(x => x.Price == targetPrice && x.Limit > this.MinLimitByAlgoritm[myOrder.AlgorithmName]);
							if (existingCorrectOrder != null)
							{
								needRaise = false;
								break;
							}

							if (myOrder.Price <= targetOrder.Price)
							{
								Console.ForegroundColor = ConsoleColor.Yellow;
								Console.WriteLine($"Повышаем цену и скорость ордера {myOrder.Id}");
								var newLimit = botSettings.limitIncrease;
								ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), newLimit.ToString(new CultureInfo("en-US")));
								processedCombinations.Add(combinationKey);

								var lowerOrders = myOrders.Where(x => x.Id != myOrder.Id && x.AlgorithmName == myOrder.AlgorithmName && x.MarketName == myOrder.MarketName && x.Price < targetPrice && x.Price > 0.0001f).ToList();
								foreach (var lowerOrder in lowerOrders)
								{
									ac.updateOrder(lowerOrder.AlgorithmName, lowerOrder.Id, lowerOrder.Price.ToString(new CultureInfo("en-US")), newLimit.ToString(new CultureInfo("en-US")));
								}
							}
							else if (myOrder.Price > targetOrder.Price)
							{
								slowDownOrder(myOrder);
								break;
							}

							break;
						}
					}
				}
			}

			toolStripStatusLabel1.Text = "Idle";
		}

		private void slowDownOrder(MyOrder myOrder)
		{
			var decreasedPrice = myOrder.Price + this.DownStepByAlgoritm[myOrder.AlgorithmName];
			// Понизить limit.
			ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, myOrder.Price.ToString(new CultureInfo("en-US")), "0.01");
			// Понизить price.
			ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, decreasedPrice.ToString(new CultureInfo("en-US")), "0.01");
		}

		private List<Orders> getAllOrders(List<string> marketNames, JArray jAlgorithms, List<string> myAlgorithmNames, List<string> myMarketNames)
		{
			List<Orders> allBookOrders = new List<Orders>();
			this.MinLimitByAlgoritm.Clear();
			this.DownStepByAlgoritm.Clear();

			foreach (var jAlgorithm in jAlgorithms)
			{
				var algorithmName = jAlgorithm["algorithm"].ToString();
				if (!myAlgorithmNames.Contains(algorithmName)) continue;

				var minSpeedLimit = (float)Math.Round(Convert.ToDouble(jAlgorithm["minSpeedLimit"].ToString(), new CultureInfo("en-US")), 4);
				this.MinLimitByAlgoritm.Add(algorithmName, minSpeedLimit);

				var priceDownStep = (float)Math.Round(Convert.ToDouble(jAlgorithm["priceDownStep"].ToString(), new CultureInfo("en-US")), 4);
				this.DownStepByAlgoritm.Add(algorithmName, priceDownStep);

				var jOrders = ac.getOrderBookWebRequest(algorithmName);

				var jStats = jOrders["stats"];

				foreach (var marketName in marketNames)
				{
					if (!myMarketNames.Contains(marketName)) continue;

					var jMarketOrders = jStats[marketName]?["orders"];

					var totalSpeed = jStats[marketName]?["totalSpeed"].ToString();
					var key = $"{algorithmName}.{marketName}";
					if (totalSpeed != null && !this.TotalSpeedByMarket.ContainsKey(key))
						this.TotalSpeedByMarket.Add(key, 0);
					this.TotalSpeedByMarket[key] = (float)Math.Round(Convert.ToDouble(totalSpeed, new CultureInfo("en-US")), 4);

					if (jMarketOrders != null)
					{
						var marketBookOrders = JsonConvert.DeserializeObject<List<Orders>>(jMarketOrders.ToString());
						marketBookOrders.ForEach(x => { x.MarketName = marketName; x.AlgorithmName = algorithmName; });
						allBookOrders.AddRange(marketBookOrders);
					}
				}
			}
			return allBookOrders.Where(x => x.Alive).ToList();
		}

		private List<MyOrder> getMyOrders()
		{
			var myOrders = new List<MyOrder>();
			foreach (var jOrder in orders)
			{
				var myOrder = JsonConvert.DeserializeObject<MyOrder>(jOrder.ToString());
				myOrder.MarketName = jOrder["market"].ToString();
				myOrder.AlgorithmName = jOrder["algorithm"]["algorithm"].ToString();
				myOrders.Add(myOrder);
			}
			return myOrders.OrderByDescending(x => x.Price).ToList();
		}

		private static float getJsonPrice(string url)
		{
			var request = WebRequest.Create(url);
			var response = request.GetResponse();
			float jsonPrice = 0;
			using (Stream dataStream = response.GetResponseStream())
			{
				var reader = new StreamReader(dataStream);
				var responseFromServer = reader.ReadToEnd();
				var dynamicJson = JsonConvert.DeserializeObject<dynamic>(responseFromServer);
				jsonPrice = (float)Math.Round(Convert.ToDouble(dynamicJson.btc_revenue.Value, new CultureInfo("en-US")), 4);
			}
			return jsonPrice;
		}

		private Dictionary<string, float> getOrderPriceRangesForAlgoAndMarket(string oa, string om)
		{
			var prices = new Dictionary<string, float>();

			foreach (JObject order in market)
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

		private void clearOrders()
		{
			var orders = ac.getOrders();
			foreach (var order in orders)
			{
				ac.cancelOrder(order["id"].ToString());
			}
		}

		private void HandleException(Exception ex)
		{
			var message = $"Ошибка в боте с ID ({botId}).\n\nТекст ошибки:\n\n{ex.Message}\n\nОжидание {botSettings.errorDelay} секунд перед перезапуском";

			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(message);

			string urlString = $"https://api.telegram.org/bot{botSettings.tgBotToken}/sendMessage?chat_id={botSettings.tgChatId}&text={message}";
			WebClient webclient = new WebClient();
			webclient.DownloadString(urlString);

			Thread.Sleep(TimeSpan.FromSeconds(botSettings.errorDelay));

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("Перезапуск бота");
		}
	}
}