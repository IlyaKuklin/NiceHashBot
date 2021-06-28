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

		private BotSettings botSettings;

		private readonly string botId;

		private long iteration = 1;

		private bool isErrorState = false;

		private bool cycleIsActive = false;

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

				var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
				this.OrdersMetadataList = JsonConvert.DeserializeObject<List<MyOrderMetadata>>(File.ReadAllText(metadataFileName));
				var myOrders = getMyOrders();
				var cancelledOrderIds = new List<string>();
				foreach (var metadata in this.OrdersMetadataList)
				{
					if (myOrders.FirstOrDefault(x => x.Id == metadata.Id) == null)
						cancelledOrderIds.Add(metadata.Id);
				}
				this.OrdersMetadataList = this.OrdersMetadataList.Where(x => !cancelledOrderIds.Contains(x.Id)).ToList();


				Console.WriteLine($"Настройки загружены. Бот {botId} запущен.\b");
				Console.WriteLine("\n***");
				Console.WriteLine(
					$"runBotDelay: {botSettings.RunBotDelay}\n" +
					$"jsonSettingsUrl: {botSettings.JsonSettingsUrl}\n" +
					$"minStepsCountToFindOrder: {botSettings.MinStepsCountToFindOrder}\n" +
					$"runRefillDelay: {botSettings.RunRefillDelay}\n" +
					$"refillOrderLimit: {botSettings.RefillOrderLimit}\n" +
					$"refillOrderAmount: {botSettings.RefillOrderAmount}\n" +
					$"tgBotToken: {botSettings.TgBotToken}\n" +
					$"tgChatId: {botSettings.TgChatId}\n" +
					$"errorDelay: {botSettings.ErrorDelay}"
					);
				Console.WriteLine("***\n");

				foreach (var jAlgorithm in ac.algorithms)
				{
					var algorithmName = jAlgorithm["algorithm"].ToString();
					var minSpeedLimit = (float)Math.Round(Convert.ToDouble(jAlgorithm["minSpeedLimit"].ToString(), new CultureInfo("en-US")), 4);
					this.MinLimitByAlgoritm.Add(algorithmName, minSpeedLimit);

					var priceDownStep = (float)Math.Round(Convert.ToDouble(jAlgorithm["priceDownStep"].ToString(), new CultureInfo("en-US")), 4);
					this.DownStepByAlgoritm.Add(algorithmName, priceDownStep);
				}

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
					TimeSpan.FromSeconds(botSettings.RunBotDelay));
			}
		}

		public Dictionary<string, float> TotalSpeedByMarket { get; set; } = new Dictionary<string, float>();
		public Dictionary<string, float> MinLimitByAlgoritm { get; set; } = new Dictionary<string, float>();
		public Dictionary<string, float> DownStepByAlgoritm { get; set; } = new Dictionary<string, float>();

		//public Dictionary<string, long> LastTimeSlowedDownTimestamps { get; set; } = new Dictionary<string, long>();
		public List<MyOrderMetadata> OrdersMetadataList { get; set; }

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
			if (!botRunning || isErrorState || cycleIsActive)
				return;

			toolStripStatusLabel1.Text = "Working";

			Console.CursorSize = 10;

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"***Начало цикла {iteration}***\n");
			cycleIsActive = true;

			Control.CheckForIllegalCrossThreadCalls = false;

			refreshOrders(true);

			JArray jAlgorithms = ac.algorithms;

			var myOrders = getMyOrders();

			var myAlgorithmNames = myOrders.Select(x => x.AlgorithmName).Distinct().ToList();
			var myMarketNames = myOrders.Select(x => x.MarketName).Distinct().ToList();
			var myOrderIds = myOrders.Select(x => x.Id).ToList();
			var allBookOrders = getAllOrders(marketNames, jAlgorithms, myAlgorithmNames, myMarketNames).Where(x => !myOrderIds.Contains(x.Id)).ToList();
			var jsonPrice = getJsonPrice(botSettings.JsonSettingsUrl);

			String fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
			botSettings = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(fileName));
			if (botSettings.JsonPrice != 0)
				jsonPrice = botSettings.JsonPrice;

			var marketSettings = botSettings.MarketSettings;
			var marketSettingNames = marketSettings.Select(x => x.Name).ToList();

			var missingMarketNames = myMarketNames.Where(x1 => marketSettingNames.All(x2 => x2 != x1)).ToList();
			if (missingMarketNames.Count > 0)
				WarnConsole($"В настройках не найдены настройки для маркетов: {string.Join(", ", missingMarketNames)}. Маркеты не будут обработаны");

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

					var currentMarketSettings = marketSettings.FirstOrDefault(x => x.Name == marketKey);
					if (currentMarketSettings == null)
					{
						WarnConsole($"\t[{algoKey}]\tНе задана настройка маркета [{marketKey}]");
						continue;
					}

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
					//var myMainOrder = myAlgMarketOrders.OrderByDescending(x => x.Price).FirstOrDefault(x => x.Price < jsonPrice && x.Price > 0.0001f);
					var myMainOrder = myAlgMarketOrders.OrderByDescending(x => x.Price).FirstOrDefault(x => x.Price > 0.0001f);

					if (myMainOrder != null)
					{
						Console.WriteLine($"\t[{algoKey}]\tНаш самый дорогой ордер имеет цену {myMainOrder.Price}");
						if (myMainOrder.Price > targetPrice)
						{
							Console.WriteLine($"\t[{algoKey}]\tЦена самого дорогого ордера выше цены конкурирующего. Попытка снизить цену.");
							// Пытаемся снизить цену.
							var slowDonwResult = slowDownOrder(myMainOrder, targetPrice - this.DownStepByAlgoritm[myMainOrder.AlgorithmName], false);
							if (slowDonwResult == SlowDownResult.Ok)
							{
								Console.WriteLine($"\t[{algoKey}]\tЦена ордера установлена на {targetPrice}");
								newMainOrder = myMainOrder;
							}
							// Не получилось снизить цену.
							else
							{
								Console.WriteLine($"\t[{algoKey}]\tСнижение цены не удалось, поиск ближайшего ордера в пределах {botSettings.MinStepsCountToFindOrder} минимальных шагов по отношению к цене {targetPrice}");

								var myNextUpperOrder =
									myAlgMarketOrders
									.OrderByDescending(x => x.Price)
									.FirstOrDefault(x => x.Id != myMainOrder.Id && x.Price < myMainOrder.Price && x.Price >= targetOrder.Price && x.Price < (targetOrder.Price + Math.Abs(this.DownStepByAlgoritm[algoKey]) * botSettings.MinStepsCountToFindOrder) && x.Price < jsonPrice);

								if (myNextUpperOrder != null)
								{
									Console.WriteLine($"\t[{algoKey}]\tНайден ордер с ценой {myNextUpperOrder.Price}");
									if (myNextUpperOrder.Limit == currentMarketSettings.MaxLimitSpeed)
									{
										Console.WriteLine($"\t[{algoKey}]\tУ ордера уже установлен max limit.");
									}
									else
									{
										Console.WriteLine($"\t[{algoKey}]\tУстанавливаем новую скорость ({currentMarketSettings.MaxLimitSpeed})");
										var updateResult = ac.updateOrder(algoKey, myNextUpperOrder.Id, myNextUpperOrder.Price.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
										if (updateResult.HasValues)
										{
											Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена");
										}
									}
									newMainOrder = myNextUpperOrder;
									targetPrice = myNextUpperOrder.Price;
								}
								else
								{
									Console.WriteLine($"\t[{algoKey}]\tОрдер в пределах {botSettings.MinStepsCountToFindOrder} минимальных шагов не найден.");
									//newMainOrder = getNextFreeOrder(algoKey, targetOrder, myAlgMarketOrders, targetPrice, currentMarketSettings.MaxLimitSpeed);
									//if (newMainOrder == null) continue;
								}
							}
						}
						else if (myMainOrder.Price == targetPrice)
						{
							if (myMainOrder.Limit != currentMarketSettings.MaxLimitSpeed)
								ac.updateOrder(algoKey, myMainOrder.Id, myMainOrder.Price.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
							Console.WriteLine($"\t[{algoKey}]\tГлавный ордер стоит перед конкурирующим. Изменения не требуются.");
							newMainOrder = myMainOrder;
						}
						else
						{
							Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера ниже цены конкурирующего. Повышение цены.");
							ac.updateOrder(algoKey, myMainOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
							Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена до {targetPrice}");
							newMainOrder = myMainOrder;
						}
					}

					if (newMainOrder == null)
						newMainOrder = getNextFreeOrder(algoKey, targetOrder, myAlgMarketOrders, targetPrice, currentMarketSettings.MaxLimitSpeed);
					if (newMainOrder == null)
					{
						WarnConsole($"\t[{algoKey}]\tНе найден подходящий ордер с ценой ниже цены JSON. Понижение скорости всем ордерам с ценой выше {targetPrice}");
						var uppers = myAlgMarketOrders.Where(x => x.Price > targetPrice).ToList();
						uppers.ForEach(x => slowDownOrder(x, x.Price));
						continue;
					}

					Console.WriteLine($"\t[{algoKey}]\tНаш текущий главный ордер имеет Id {newMainOrder.Id}");

					var lowerOrders = myAlgMarketOrders.Where(x => x.Id != newMainOrder.Id && x.Price < targetPrice && x.Price > 0.0001f).ToList();
					Console.WriteLine($"\t[{algoKey}]\tУстанавливаем max limit и понижаем цену активным ордерам ниже главного ({lowerOrders.Count} шт)");
					lowerOrders.ForEach(order =>
					{
						if (order.Limit < currentMarketSettings.MaxLimitSpeed)
							ac.updateOrder(order.AlgorithmName, order.Id, order.Price.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
						slowDownOrder(order, order.Price, false);
					});

					var upperOrders = myAlgMarketOrders.Where(x => x.Id != newMainOrder.Id && x.Price > targetPrice).ToList();
					Console.WriteLine($"\t[{algoKey}]\tПонижаем limit и speed активным ордерам выше главного ({upperOrders.Count} шт)");
					upperOrders.ForEach(order =>
					{
						slowDownOrder(order, order.Price);
					});
				}
			}

			var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
			if (iteration > 1 && (currentTimeStamp - lastRunStamp) >= botSettings.RunRefillDelay)
			{
				Console.WriteLine("\nRefill orders start");

				myOrders.ForEach(order =>
				{
					var ok = (order.AvailableAmount - order.PayedAmount) < botSettings.RefillOrderLimit;
					if (ok)
						ac.refillOrder(order.Id, botSettings.RefillOrderAmount.ToString(new CultureInfo("en-US")));
				});
				Console.WriteLine("Refill orders end\n");
				lastRunStamp = currentTimeStamp;
			}

			toolStripStatusLabel1.Text = "Idle";

			iteration++;

			var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
			File.WriteAllText(metadataFileName, JsonConvert.SerializeObject(this.OrdersMetadataList));

			Console.WriteLine("\n***Окончание цикла***\n");
			cycleIsActive = false;
		}

		private MyOrder getNextFreeOrder(string algoKey, BookOrder targetOrder, List<MyOrder> myAlgMarketOrders, float targetPrice, float maxLimitSpeed)
		{
			Console.WriteLine($"\t[{algoKey}]\tПоиск ближайшего ордера снизу от конкурирующего");
			var myNextOrder = myAlgMarketOrders.FirstOrDefault(x => x.Price < targetOrder.Price);
			if (myNextOrder != null)
			{
				Console.WriteLine($"\t[{algoKey}]\tНайден ордер {myNextOrder.Id}. Устанавливаем цену {targetPrice} и скорость {maxLimitSpeed}");
				ac.updateOrder(algoKey, myNextOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), maxLimitSpeed.ToString(new CultureInfo("en-US")));
				return myNextOrder;
			}
			else
			{
				WarnConsole($"\t[{algoKey}]\tНе осталось свободных ордеров для повышения цены");
				return null;
			}
		}

		private static BookOrder getTargetBookOrderInAlgoAndMarket(string algoKey, float totalLimit, List<BookOrder> targetBookOrders, ref float currentLimit)
		{
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

		private SlowDownResult slowDownOrder(MyOrder myOrder, float price, bool slowDownLimit = true)
		{
			var result = SlowDownResult.Undefined;

			var decreasedPrice = price + this.DownStepByAlgoritm[myOrder.AlgorithmName];
			if (price <= 0)
				throw new ApplicationException("Ошибка понижения цены");

			// Понизить limit.
			if (slowDownLimit && myOrder.Limit > this.MinLimitByAlgoritm[myOrder.AlgorithmName])
				ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, myOrder.Price.ToString(new CultureInfo("en-US")), this.MinLimitByAlgoritm[myOrder.AlgorithmName].ToString(new CultureInfo("en-US")));

			var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();

			var metadata = this.OrdersMetadataList.FirstOrDefault(x => x.Id == myOrder.Id);
			if (metadata == null)
			{
				metadata = new MyOrderMetadata { Id = myOrder.Id, LastPriceDecreasedTime = 0 };
				this.OrdersMetadataList.Add(metadata);
			}

			var difference = currentTimeStamp - metadata.LastPriceDecreasedTime;
			long slowDownDelay = 600;

			if (difference > slowDownDelay)
			{
				var limit = slowDownLimit ? this.MinLimitByAlgoritm[myOrder.AlgorithmName] : myOrder.Limit;

				// Понизить price.
				var updateResult = ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, decreasedPrice.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")));
				if (updateResult.HasValues)
				{
					metadata.LastPriceDecreasedTime = currentTimeStamp;
					result = SlowDownResult.Ok;
				}
				else
				{
					result = SlowDownResult.ApiError;
				}
			}

			return result;
		}

		private List<BookOrder> getAllOrders(List<string> marketNames, JArray jAlgorithms, List<string> myAlgorithmNames, List<string> myMarketNames)
		{
			List<BookOrder> allBookOrders = new List<BookOrder>();

			foreach (var jAlgorithm in jAlgorithms)
			{
				var algorithmName = jAlgorithm["algorithm"].ToString();
				if (!myAlgorithmNames.Contains(algorithmName)) continue;

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
			var message = $"Ошибка в боте с ID ({botId}).\n\nТекст ошибки:\n\n{ex}\n\nОжидание {botSettings.ErrorDelay} секунд перед перезапуском";

			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(message);

			string urlString = $"https://api.telegram.org/bot{botSettings.TgBotToken}/sendMessage?chat_id={botSettings.TgChatId}&text={message}";
			WebClient webclient = new WebClient();
			webclient.DownloadString(urlString);

			isErrorState = true;
			Thread.Sleep(TimeSpan.FromSeconds(botSettings.ErrorDelay));
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

	public enum SlowDownResult 
	{
		Undefined,
		Ok,
		ApiError

	}

}