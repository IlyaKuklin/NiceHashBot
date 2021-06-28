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
using Timer = System.Threading.Timer;

namespace NHB3
{
	public partial class Home : Form
	{
		private readonly ApiConnect _ac;
		private readonly string _currency = "TBTC";
		private bool _botRunning = false;
		private JArray _orders;
		private JArray _market;
		private readonly List<string> _marketNames;
		private readonly Timer _timer;
		private BotSettings _botSettings;
		private readonly string _botId;
		private long _iteration = 1;
		private bool _isErrorState = false;
		private bool _cycleIsActive = false;

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

				//clearOrders();

				if (acSettings.Enviorment == 1)
					_currency = "BTC";
				_ac.currency = _currency;

				refreshBalance();
				refreshOrders(false);
				_ac.getPools(true);

				_marketNames = _ac.getMarkets();

				var fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
				if (!File.Exists(fileName))
					return;
				try
				{
					_botSettings = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(fileName));
				}
				catch
				{
					WarnConsole("Ошибка загрузки настроек, проверьте корректность ввода данных");
					return;
				}

				var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
				this.OrdersMetadataList = JsonConvert.DeserializeObject<List<MyOrderMetadata>>(File.ReadAllText(metadataFileName));

				var myOrders = GetMyOrders();
				var cancelledOrderIds = new List<string>();
				foreach (var metadata in this.OrdersMetadataList)
				{
					if (myOrders.FirstOrDefault(x => x.Id == metadata.Id) == null)
						cancelledOrderIds.Add(metadata.Id);
				}
				this.OrdersMetadataList = this.OrdersMetadataList.Where(x => !cancelledOrderIds.Contains(x.Id)).ToList();

				Console.WriteLine($"Настройки загружены. Бот {_botId} запущен.\b");
				Console.WriteLine("\n***");
				Console.WriteLine(
					$"runBotDelay: {_botSettings.RunBotDelay}\n" +
					$"jsonSettingsUrl: {_botSettings.JsonSettingsUrl}\n" +
					$"minStepsCountToFindOrder: {_botSettings.MinStepsCountToFindOrder}\n" +
					$"runRefillDelay: {_botSettings.RunRefillDelay}\n" +
					$"refillOrderLimit: {_botSettings.RefillOrderLimit}\n" +
					$"refillOrderAmount: {_botSettings.RefillOrderAmount}\n" +
					$"tgBotToken: {_botSettings.TgBotToken}\n" +
					$"tgChatId: {_botSettings.TgChatId}\n" +
					$"errorDelay: {_botSettings.ErrorDelay}"
					);
				Console.WriteLine("***\n");

				foreach (var jAlgorithm in _ac.algorithms)
				{
					var algorithmName = jAlgorithm["algorithm"].ToString();
					var minSpeedLimit = (float)Math.Round(Convert.ToDouble(jAlgorithm["minSpeedLimit"].ToString(), new CultureInfo("en-US")), 4);
					this.MinLimitByAlgoritm.Add(algorithmName, minSpeedLimit);

					var priceDownStep = (float)Math.Round(Convert.ToDouble(jAlgorithm["priceDownStep"].ToString(), new CultureInfo("en-US")), 4);
					this.DownStepByAlgoritm.Add(algorithmName, priceDownStep);
				}

				_timer = new Timer(
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
					TimeSpan.FromSeconds(_botSettings.RunBotDelay));
			}
		}

		public Dictionary<string, float> TotalSpeedByMarket { get; set; } = new Dictionary<string, float>();
		public Dictionary<string, float> MinLimitByAlgoritm { get; set; } = new Dictionary<string, float>();
		public Dictionary<string, float> DownStepByAlgoritm { get; set; } = new Dictionary<string, float>();
		public List<MyOrderMetadata> OrdersMetadataList { get; set; }

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

		private void balanceToolStripMenuItem_Click(object sender, EventArgs e)
		{
			refreshBalance();
		}

		private void refreshBalance()
		{
			if (_ac.connected)
			{
				JObject balance = _ac.getBalance(_currency);
				if (balance != null)
				{
					this.toolStripStatusLabel2.Text = "Balance: " + balance["available"] + " " + _currency;
				}
			}
			else
			{
				this.toolStripStatusLabel2.Text = "Balance: N/A " + _currency;
			}
		}

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
			_botRunning = false;
		}

		private void autoPilotONToolStripMenuItem_Click(object sender, EventArgs e)
		{
			toolStripStatusLabel1.Text = "Idle";
			_botRunning = true;
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

		private void runBot()
		{
			// START
			if (!_botRunning || _isErrorState || _cycleIsActive)
				return;

			toolStripStatusLabel1.Text = "Working";

			Console.CursorSize = 10;

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"***Начало цикла {_iteration}***\n");
			_cycleIsActive = true;

			CheckForIllegalCrossThreadCalls = false;

			refreshOrders(true);

			var jAlgorithms = _ac.algorithms;

			var myOrders = GetMyOrders();

			var myAlgorithmNames = myOrders.Select(x => x.AlgorithmName).Distinct().ToList();
			var myMarketNames = myOrders.Select(x => x.MarketName).Distinct().ToList();
			var myOrderIds = myOrders.Select(x => x.Id).ToList();
			var allBookOrders = GetAllOrders(_marketNames, jAlgorithms, myAlgorithmNames, myMarketNames).Where(x => !myOrderIds.Contains(x.Id)).ToList();
			var jsonPrice = GetJsonPrice(_botSettings.JsonSettingsUrl);

			var fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
			_botSettings = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(fileName));
			if (_botSettings.JsonPrice != 0)
				jsonPrice = _botSettings.JsonPrice;

			var marketSettings = _botSettings.MarketSettings;
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
					var targetOrder = GetTargetBookOrderInAlgoAndMarket(algoKey, totalLimit, targetBookOrders, ref currentLimit);
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
							var slowDonwResult = SlowDownOrder(myMainOrder, targetPrice - this.DownStepByAlgoritm[myMainOrder.AlgorithmName], false);
							if (slowDonwResult == SlowDownResult.Ok)
							{
								Console.WriteLine($"\t[{algoKey}]\tЦена ордера установлена на {targetPrice}");
								newMainOrder = myMainOrder;
							}
							// Не получилось снизить цену.
							else
							{
								Console.WriteLine($"\t[{algoKey}]\tСнижение цены не удалось, поиск ближайшего ордера в пределах {_botSettings.MinStepsCountToFindOrder} минимальных шагов по отношению к цене {targetPrice}");

								var myNextUpperOrder =
									myAlgMarketOrders
									.OrderByDescending(x => x.Price)
									.FirstOrDefault(x => x.Id != myMainOrder.Id && x.Price < myMainOrder.Price && x.Price >= targetOrder.Price && x.Price < (targetOrder.Price + Math.Abs(this.DownStepByAlgoritm[algoKey]) * _botSettings.MinStepsCountToFindOrder) && x.Price < jsonPrice);

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
										var updateResult = _ac.updateOrder(algoKey, myNextUpperOrder.Id, myNextUpperOrder.Price.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
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
									Console.WriteLine($"\t[{algoKey}]\tОрдер в пределах {_botSettings.MinStepsCountToFindOrder} минимальных шагов не найден.");
								}
							}
						}
						else if (myMainOrder.Price == targetPrice)
						{
							if (myMainOrder.Limit != currentMarketSettings.MaxLimitSpeed)
								_ac.updateOrder(algoKey, myMainOrder.Id, myMainOrder.Price.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
							Console.WriteLine($"\t[{algoKey}]\tГлавный ордер стоит перед конкурирующим. Изменения не требуются.");
							newMainOrder = myMainOrder;
						}
						else
						{
							Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера ниже цены конкурирующего. Повышение цены.");
							_ac.updateOrder(algoKey, myMainOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
							Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена до {targetPrice}");
							newMainOrder = myMainOrder;
						}
					}

					if (newMainOrder == null)
						newMainOrder = GetNextFreeOrder(algoKey, targetOrder, myAlgMarketOrders, targetPrice, currentMarketSettings.MaxLimitSpeed);
					if (newMainOrder == null)
					{
						WarnConsole($"\t[{algoKey}]\tНе найден подходящий ордер с ценой ниже цены JSON. Понижение скорости всем ордерам с ценой выше {targetPrice}");
						var uppers = myAlgMarketOrders.Where(x => x.Price > targetPrice).ToList();
						uppers.ForEach(x => SlowDownOrder(x, x.Price));
						continue;
					}

					Console.WriteLine($"\t[{algoKey}]\tНаш текущий главный ордер имеет Id {newMainOrder.Id}");

					var lowerOrders = myAlgMarketOrders.Where(x => x.Id != newMainOrder.Id && x.Price < targetPrice && x.Price > 0.0001f).ToList();
					Console.WriteLine($"\t[{algoKey}]\tУстанавливаем max limit и понижаем цену активным ордерам ниже главного ({lowerOrders.Count} шт)");
					lowerOrders.ForEach(order =>
					{
						if (order.Limit < currentMarketSettings.MaxLimitSpeed)
							_ac.updateOrder(order.AlgorithmName, order.Id, order.Price.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")));
						SlowDownOrder(order, order.Price, false);
					});

					var upperOrders = myAlgMarketOrders.Where(x => x.Id != newMainOrder.Id && x.Price > targetPrice).ToList();
					Console.WriteLine($"\t[{algoKey}]\tПонижаем limit и speed активным ордерам выше главного ({upperOrders.Count} шт)");
					upperOrders.ForEach(order =>
					{
						SlowDownOrder(order, order.Price);
					});
				}
			}

			var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
			if (_iteration > 1 && (currentTimeStamp - lastRunStamp) >= _botSettings.RunRefillDelay)
			{
				Console.WriteLine("\nRefill orders start");

				myOrders.ForEach(order =>
				{
					var ok = (order.AvailableAmount - order.PayedAmount) < _botSettings.RefillOrderLimit;
					if (ok)
						_ac.refillOrder(order.Id, _botSettings.RefillOrderAmount.ToString(new CultureInfo("en-US")));
				});
				Console.WriteLine("Refill orders end\n");
				lastRunStamp = currentTimeStamp;
			}

			toolStripStatusLabel1.Text = "Idle";

			_iteration++;

			var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
			File.WriteAllText(metadataFileName, JsonConvert.SerializeObject(this.OrdersMetadataList));

			Console.WriteLine("\n***Окончание цикла***\n");
			_cycleIsActive = false;
		}

		private MyOrder GetNextFreeOrder(string algoKey, BookOrder targetOrder, List<MyOrder> myAlgMarketOrders, float targetPrice, float maxLimitSpeed)
		{
			Console.WriteLine($"\t[{algoKey}]\tПоиск ближайшего ордера снизу от конкурирующего");
			var myNextOrder = myAlgMarketOrders.FirstOrDefault(x => x.Price < targetOrder.Price);
			if (myNextOrder != null)
			{
				Console.WriteLine($"\t[{algoKey}]\tНайден ордер {myNextOrder.Id}. Устанавливаем цену {targetPrice} и скорость {maxLimitSpeed}");
				_ac.updateOrder(algoKey, myNextOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), maxLimitSpeed.ToString(new CultureInfo("en-US")));
				return myNextOrder;
			}
			else
			{
				WarnConsole($"\t[{algoKey}]\tНе осталось свободных ордеров для повышения цены");
				return null;
			}
		}

		private static BookOrder GetTargetBookOrderInAlgoAndMarket(string algoKey, float totalLimit, List<BookOrder> targetBookOrders, ref float currentLimit)
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

		private SlowDownResult SlowDownOrder(MyOrder myOrder, float price, bool slowDownLimit = true)
		{
			var result = SlowDownResult.Undefined;

			var decreasedPrice = price + this.DownStepByAlgoritm[myOrder.AlgorithmName];
			if (price <= 0)
				throw new ApplicationException("Ошибка понижения цены");

			// Понизить limit.
			if (slowDownLimit && myOrder.Limit > this.MinLimitByAlgoritm[myOrder.AlgorithmName])
				_ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, myOrder.Price.ToString(new CultureInfo("en-US")), this.MinLimitByAlgoritm[myOrder.AlgorithmName].ToString(new CultureInfo("en-US")));

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
				var updateResult = _ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, decreasedPrice.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")));
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

		private List<BookOrder> GetAllOrders(List<string> marketNames, JArray jAlgorithms, List<string> myAlgorithmNames, List<string> myMarketNames)
		{
			var allBookOrders = new List<BookOrder>();

			foreach (var jAlgorithm in jAlgorithms)
			{
				var algorithmName = jAlgorithm["algorithm"].ToString();
				if (!myAlgorithmNames.Contains(algorithmName)) continue;

				var jOrders = _ac.getOrderBookWebRequest(algorithmName);

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

		private List<MyOrder> GetMyOrders()
		{
			var myOrders = new List<MyOrder>();
			foreach (var jOrder in _orders)
			{
				var myOrder = JsonConvert.DeserializeObject<MyOrder>(jOrder.ToString());
				myOrder.MarketName = jOrder["market"].ToString();
				myOrder.AlgorithmName = jOrder["algorithm"]["algorithm"].ToString();
				myOrders.Add(myOrder);
			}
			return myOrders.OrderByDescending(x => x.Price).ToList();
		}

		private static float GetJsonPrice(string url)
		{
			try
			{
				float jsonPrice = 0;
				var request = WebRequest.Create(url);
				var response = request.GetResponse();
				using (var dataStream = response.GetResponseStream())
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

			string urlString = $"https://api.telegram.org/bot{_botSettings.TgBotToken}/sendMessage?chat_id={_botSettings.TgChatId}&text={message}";
			WebClient webclient = new WebClient();
			webclient.DownloadString(urlString);

			_isErrorState = true;
			Thread.Sleep(TimeSpan.FromSeconds(_botSettings.ErrorDelay));
			_isErrorState = false;

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