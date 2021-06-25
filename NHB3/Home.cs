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

		private long iteration = 1;

		private bool isErrorState = false;

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

				try
				{
					botSettings = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(fileName));
				}
				catch
				{
					WarnConsole("Ошибка загрузки настроек, проверьте корректность ввода данных");
					return;
				}

				Console.WriteLine($"Настройки загружены. Бот {botId} запущен.\b");
				Console.WriteLine("\n***");
				Console.WriteLine(
					$"runBotDelay:\t {botSettings.runBotDelay}\n" +
					$"maxLimitSpeed: {botSettings.maxLimitSpeed}\n" +
					$"jsonSettingsUrl: {botSettings.jsonSettingsUrl}\n" +
					$"minStepsCountToFindOrder: {botSettings.minStepsCountToFindOrder}\n" +
					$"runRefillDelay: {botSettings.runRefillDelay}\n" +
					$"refillOrderLimit: {botSettings.refillOrderLimit}\n" +
					$"refillOrderAmount: {botSettings.refillOrderAmount}\n" +
					$"tgBotToken: {botSettings.tgBotToken}\n" +
					$"tgChatId: {botSettings.tgChatId}\n" +
					$"errorDelay: {botSettings.errorDelay}"
					);
				Console.WriteLine("***\n");


				timer = new System.Threading.Timer(
					e =>
					{
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
			try
			{
				runBot();
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

		private long lastRunStamp;

		private void runBot()
		{
			// START
			if (!botRunning || isErrorState)
				return;

			toolStripStatusLabel1.Text = "Working";

			Console.CursorSize = 10;

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"***Начало цикла {iteration}***\n");

			Control.CheckForIllegalCrossThreadCalls = false;

			refreshOrders(true);

			JArray jAlgorithms = ac.algorithms;

			var myOrders = getMyOrders();

			var myAlgorithmNames = myOrders.Select(x => x.AlgorithmName).Distinct().ToList();
			var myMarketNames = myOrders.Select(x => x.MarketName).Distinct().ToList();
			var myOrderIds = myOrders.Select(x => x.Id).ToList();
			var allBookOrders = getAllOrders(marketNames, jAlgorithms, myAlgorithmNames, myMarketNames).Where(x => !myOrderIds.Contains(x.Id)).ToList();
			var jsonPrice = getJsonPrice(botSettings.jsonSettingsUrl);

			//jsonPrice = 1.800f;

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"Цена json: {jsonPrice}");

			var bookOrdersByAlgorithms = allBookOrders.GroupBy(x => x.AlgorithmName).ToDictionary(x => x.Key, x => x.ToList());
			var myOrdersByAlgorithms = myOrders.GroupBy(x => x.AlgorithmName).ToDictionary(x => x.Key, x => x.ToList()).OrderBy(x => x.Key);

			foreach (var algorithmKVP in myOrdersByAlgorithms)
			{
				var algoKey = algorithmKVP.Key;
				Console.WriteLine($"Обработка алгоритма [{algoKey}]\n");

				var myAlgOrders = algorithmKVP.Value;
				var bookAlgOrders = bookOrdersByAlgorithms[algoKey];

				var myAlgOrdersByMarket = myAlgOrders.GroupBy(x => x.MarketName).ToDictionary(x => x.Key, x => x.ToList()).OrderBy(x => x.Key);
				var bookAlgOrdersByMarket = bookAlgOrders.GroupBy(x => x.MarketName).ToDictionary(x => x.Key, x => x.ToList());

				foreach (var myAlgMarketOrdersKVP in myAlgOrdersByMarket)
				{
					var marketKey = myAlgMarketOrdersKVP.Key;
					var totalLimit = this.TotalSpeedByMarket[$"{algoKey}.{marketKey}"];

					Console.WriteLine("\t***");
					Console.WriteLine($"\t[{algoKey}]\tОбработка маркета [{marketKey}]");

					var myAlgMarketOrders = myAlgMarketOrdersKVP.Value;
					var bookAlgMarketOrders = bookAlgOrdersByMarket[marketKey];

					var targetBookOrders = bookAlgMarketOrders
						.Where(x => x.Price < jsonPrice)
						.OrderByDescending(x => x.Price)
						.ToList();

					var currentLimit = 0.0f;
					var targetOrder = getTargetBookOrderInAlgoAndMarket(algoKey, totalLimit, targetBookOrders, ref currentLimit);
					if (targetOrder == null)
					{
						WarnConsole("Не найден подходящий чужой ордер");
						continue;
					}
					Console.WriteLine($"\t[{algoKey}]\tЦена, скорость, id конкурирующего ордера: {targetOrder.Price} | {targetOrder.Limit} | {targetOrder.Id}");

					MyOrder newMainOrder = null;
					var targetPrice = (float)Math.Round(targetOrder.Price + 0.0001f, 4);
					var myMainOrder = myAlgMarketOrders.OrderByDescending(x => x.Price).FirstOrDefault(x => x.Price < jsonPrice && x.Limit > this.MinLimitByAlgoritm[algoKey]);
					if (myMainOrder != null)
					{
						Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера: {myMainOrder.Price}");
						if (myMainOrder.Price > targetPrice)
						{
							Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера выше цены конкурирующего. Попытка снизить цену.");
							// Пытаемся снизить цену.
							var updateResult = ac.updateOrder(algoKey, myMainOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), botSettings.maxLimitSpeed.ToString(new CultureInfo("en-US")));
							if (updateResult.HasValues)
							{
								Console.WriteLine($"\t[{algoKey}]\tЦена ордера установлена на {targetPrice}");
								newMainOrder = myMainOrder;
							}
							// Не получилось снизить цену.
							else
							{
								Console.WriteLine($"\t[{algoKey}]\tСнижение цены не удалось, поиск ордера в пределах {botSettings.minStepsCountToFindOrder} минимальных шагов");
								var myNextUpperOrder =
									myAlgMarketOrders
									.OrderByDescending(x => x.Price)
									.FirstOrDefault(x => x.Price >= targetOrder.Price && x.Price < (targetOrder.Price + Math.Abs(this.DownStepByAlgoritm[algoKey]) * botSettings.minStepsCountToFindOrder));

								if (myNextUpperOrder != null)
								{
									Console.WriteLine($"Найден ордер с ценой {myNextUpperOrder.Price}. Устанавливаем новую скорость ({botSettings.maxLimitSpeed})");
									updateResult = ac.updateOrder(algoKey, myNextUpperOrder.Id, myNextUpperOrder.Price.ToString(new CultureInfo("en-US")), botSettings.maxLimitSpeed.ToString(new CultureInfo("en-US")));
									if (updateResult.HasValues)
									{
										Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена");
										newMainOrder = myNextUpperOrder;
									}
								}
								else
								{
									Console.WriteLine($"\t[{algoKey}]\tОрдер в пределах {botSettings.minStepsCountToFindOrder} минимальных шагов не найден.");
									newMainOrder = getNextFreeOrder(algoKey, targetOrder, myAlgMarketOrders, targetPrice);
									if (newMainOrder == null) continue;
								}
							}
						}
						else if (myMainOrder.Price == targetPrice)
						{
							Console.WriteLine($"\t[{algoKey}]\tГлавный ордер стоит перед конкурирующим. Изменения не требуются.");
							continue;
						}
						else
						{
							Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера ниже цены конкурирующего. Повышение цены.");
							ac.updateOrder(algoKey, myMainOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), botSettings.maxLimitSpeed.ToString(new CultureInfo("en-US")));
							Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена до {targetPrice}");
							newMainOrder = myMainOrder;
						}
					}

					if (newMainOrder == null)
						newMainOrder = getNextFreeOrder(algoKey, targetOrder, myAlgMarketOrders, targetPrice);
					if (newMainOrder == null) continue;

					var lowerOrders = myAlgMarketOrders.Where(x => x.Id != newMainOrder.Id && x.Price < targetPrice && x.Price > 0.0001f).ToList();
					Console.WriteLine($"\t[{algoKey}]\tУстанавливаем max limit активным ордерам ниже главного ({lowerOrders.Count} шт)");
					lowerOrders.ForEach(order =>
					{
						if (order.Limit != botSettings.maxLimitSpeed && order.Price > 0.0001f)
							ac.updateOrder(order.AlgorithmName, order.Id, order.Price.ToString(new CultureInfo("en-US")), botSettings.maxLimitSpeed.ToString(new CultureInfo("en-US")));
					});

					var upperOrders = myAlgMarketOrders.Where(x => x.Id != newMainOrder.Id && x.Price > targetPrice).ToList();
					Console.WriteLine($"\t[{algoKey}]\tПонимажем limit и speed активным ордерам выше главного ({upperOrders.Count} шт)");
					upperOrders.ForEach(order =>
					{
						if (order.Limit > this.MinLimitByAlgoritm[algoKey])
							slowDownOrder(order);
					});
				}
			}

			var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
			if (iteration > 1 && (currentTimeStamp - lastRunStamp) >= botSettings.runRefillDelay)
			{
				Console.WriteLine("\nRefill orders start");

				myOrders.ForEach(order =>
				{
					if (order.AvailableAmount < botSettings.refillOrderLimit)
						ac.refillOrder(order.Id, botSettings.refillOrderAmount.ToString(new CultureInfo("en-US")));
				});
				Console.WriteLine("Refill orders end\n");
			}

			toolStripStatusLabel1.Text = "Idle";

			lastRunStamp = currentTimeStamp;
			iteration++;

			Console.WriteLine("\n***Окончание цикла***\n");
		}

		private MyOrder getNextFreeOrder(string algoKey, BookOrder targetOrder, List<MyOrder> myAlgMarketOrders, float targetPrice)
		{
			Console.WriteLine($"\t[{algoKey}]\tПоиск ближайшего ордера снизу от конкурирующего");
			var myNextOrder = myAlgMarketOrders.FirstOrDefault(x => x.Price < targetOrder.Price);
			if (myNextOrder != null)
			{
				Console.WriteLine($"\t[{algoKey}]\tНайден ордер {myNextOrder.Id}. Устанавливаем цену {targetPrice} и скорость {botSettings.maxLimitSpeed}");
				ac.updateOrder(algoKey, myNextOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), botSettings.maxLimitSpeed.ToString(new CultureInfo("en-US")));
				return myNextOrder;
			}
			else
			{
				WarnConsole("Не осталось свободных ордеров для повышения цены");
				return null;
			}
		}

		private static BookOrder getTargetBookOrderInAlgoAndMarket(string algoKey, float totalLimit, List<BookOrder> targetBookOrders, ref float currentLimit)
		{
			throw new Exception("test ss");

			BookOrder targetOrder = null;
			foreach (var targetBookOrder in targetBookOrders)
			{
				currentLimit += targetBookOrder.Limit;
				if (targetBookOrder.Limit == 0 || currentLimit >= totalLimit)
				{
					targetOrder = targetBookOrder;
					break;
				}
			}
			return targetOrder;
		}

		private void slowDownOrder(MyOrder myOrder)
		{
			var decreasedPrice = myOrder.Price + this.DownStepByAlgoritm[myOrder.AlgorithmName];
			// Понизить limit.
			ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, myOrder.Price.ToString(new CultureInfo("en-US")), "0.01");
			// Понизить price.
			ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, decreasedPrice.ToString(new CultureInfo("en-US")), "0.01");
		}

		private List<BookOrder> getAllOrders(List<string> marketNames, JArray jAlgorithms, List<string> myAlgorithmNames, List<string> myMarketNames)
		{
			List<BookOrder> allBookOrders = new List<BookOrder>();
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
						var marketBookOrders = JsonConvert.DeserializeObject<List<BookOrder>>(jMarketOrders.ToString());
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
			try
			{
				float jsonPrice = 0;
				var request = WebRequest.Create(url);
				var response = request.GetResponse();
				using (Stream dataStream = response.GetResponseStream())
				{
					var reader = new StreamReader(dataStream);
					var responseFromServer = reader.ReadToEnd();
					var dynamicJson = JsonConvert.DeserializeObject<dynamic>(responseFromServer);
					jsonPrice = (float)Math.Round(Convert.ToDouble(dynamicJson.btc_revenue.Value, new CultureInfo("en-US")), 4);
				}
				return jsonPrice;
			}
			catch (Exception ex)
			{
				throw new ApplicationException($"Ошибка при получении настроек JSON с сервера", ex);
			}
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

			isErrorState = true;
			Thread.Sleep(TimeSpan.FromSeconds(botSettings.errorDelay));
			isErrorState = false;

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("Перезапуск бота");
		}

		private void WarnConsole(string message)
		{
			Console.ForegroundColor = ConsoleColor.DarkYellow;
			Console.WriteLine(message);
			Console.ForegroundColor = ConsoleColor.White;
		}
	}
}