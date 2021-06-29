using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NHB3.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace NHB3
{
	public class Processor
	{
		private bool _cycleIsActive = false;
		private long _iteration = 1;
		private readonly List<string> _marketNames;
		private long lastRunStamp;

		private readonly ApiConnect _ac;
		private BotSettings _botSettings;
		private JArray _orders;

		private readonly string _botId;

		private bool _settingsError;

		public Processor(ApiConnect ac, BotSettings botSettings, JArray orders, string botId)
		{
			_ac = ac ?? throw new ArgumentNullException(nameof(ac));
			_botSettings = botSettings ?? throw new ArgumentNullException(nameof(botSettings));
			_orders = orders ?? throw new ArgumentNullException(nameof(orders));
			_botId = botId ?? throw new ArgumentNullException(nameof(botId));

			_marketNames = _ac.getMarkets();

			this.Initialize();
		}

		private void Initialize()
		{
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

			this.ValidateSettings();
			if (_settingsError) return;

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
		}

		private void ValidateSettings()
		{
			Console.WriteLine("Проверка настроек");
			if (_botSettings.JsonPrice > 0)
				WarnConsole("Включено переопределение json цены в настройках. Для выключения установите параметр 'jsonPrice' равным 0");

			if (_botSettings.RunBotDelay == 0)
			{
				WarnConsole("Значение runBotDelay не может быть меньше или равно 0", true);
				_settingsError = true;
			}

			if (_botSettings.RunRefillDelay == 0)
			{
				WarnConsole("Значение runRefillDelay не может быть меньше или равно 0", true);
				_settingsError = true;
			}

			if (_botSettings.AllocationSettingsOn)
			{
			}
		}

		private Dictionary<string, float> TotalSpeedByMarket { get; set; } = new Dictionary<string, float>();
		private Dictionary<string, float> MinLimitByAlgoritm { get; set; } = new Dictionary<string, float>();
		private Dictionary<string, float> DownStepByAlgoritm { get; set; } = new Dictionary<string, float>();
		private List<MyOrderMetadata> OrdersMetadataList { get; set; }
		private bool BotRunning { get; set; }
		private bool IsErrorState { get; set; }

		internal void SwitchState(bool running) => this.BotRunning = running;

		internal void SwitchErrorState(bool isErrorState) => this.IsErrorState = isErrorState;

		internal void RunBot()
		{
			// START
			if (!this.BotRunning || this.IsErrorState || _cycleIsActive)
				return;

			if (_settingsError)
			{
				WarnConsole("Ошибка настроек, логика не будет запущена. Исправьте ошибки и перезапустите бот", true);
				return;
			}

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"***Начало цикла {_iteration}***\n");
			_cycleIsActive = true;

			RefreshOrders();

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

			// Ордера, которым была установлена цена/скорость, и их обработка больше не требуется.
			var processedOrderIds = new List<string>();

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

					Order newMainOrder = null;
					var targetPrice = (float)Math.Round(targetOrder.Price + 0.0001f, 4);
					//var myMainOrder = myAlgMarketOrders.OrderByDescending(x => x.Price).FirstOrDefault(x => x.Price < jsonPrice && x.Price > 0.0001f);
					var myMainOrder = myAlgMarketOrders.OrderByDescending(x => x.Price).FirstOrDefault(x => x.Price <= jsonPrice && x.Price > 0.0001f);

					if (myMainOrder != null)
					{
						Console.WriteLine($"\t[{algoKey}]\tНаш самый дорогой ордер имеет цену {myMainOrder.Price}");
						if (myMainOrder.Price > targetPrice)
						{
							Console.WriteLine($"\t[{algoKey}]\tЦена самого дорогого ордера выше цены конкурирующего. Попытка снизить цену.");
							// Пытаемся снизить цену.
							//var slowDonwResult = SlowDownOrder(myMainOrder, targetPrice - this.DownStepByAlgoritm[myMainOrder.AlgorithmName], false);
							var updated = this.SetPrice(myMainOrder, targetPrice);
							if (updated != null)
							{
								Console.WriteLine($"\t[{algoKey}]\tЦена ордера установлена на {targetPrice}");
								newMainOrder = updated;
								//newMainOrder.Price = updated.Price;
								processedOrderIds.Add(myMainOrder.Id);
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
										myNextUpperOrder = _ac.updateOrder(algoKey, myNextUpperOrder.Id, myNextUpperOrder.Price.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")))?.Item1;
										if (myNextUpperOrder != null)
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
							myMainOrder = _ac.updateOrder(algoKey, myMainOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), currentMarketSettings.MaxLimitSpeed.ToString(new CultureInfo("en-US")))?.Item1;
							if (myMainOrder != null)
							{
								Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена до {targetPrice}");
								newMainOrder = myMainOrder;
							}
							else
								Console.WriteLine($"\t[{algoKey}]\tОшибка при повышении цены");
						}
					}

					if (newMainOrder == null)
						newMainOrder = GetNextFreeOrder(algoKey, targetOrder, myAlgMarketOrders, targetPrice, currentMarketSettings.MaxLimitSpeed);
					if (newMainOrder == null)
					{
						WarnConsole($"\t[{algoKey}]\tНе найден подходящий ордер с ценой ниже цены JSON. Понижение скорости всем ордерам с ценой выше {targetPrice}");
						var uppers = myAlgMarketOrders.Where(x => x.Price > targetPrice).ToList();
						//uppers.ForEach(x => SlowDownOrder(x, x.Price));
						uppers.ForEach(x => SetLimit(x, this.MinLimitByAlgoritm[x.AlgorithmName]));
						continue;
					}

					Console.WriteLine($"\t[{algoKey}]\tНаш текущий главный ордер имеет Id {newMainOrder.Id}");
					processedOrderIds.Add(newMainOrder.Id);

					this.FormOrdersGroup(myOrders, processedOrderIds, algoKey, currentMarketSettings, newMainOrder);

					var lowerOrders = myAlgMarketOrders.Where(x => !processedOrderIds.Contains(x.Id) && x.Price < targetPrice && x.Price > 0.0001f).ToList();
					Console.WriteLine($"\t[{algoKey}]\tУстанавливаем max limit и понижаем цену активным ордерам ниже главного ({lowerOrders.Count} шт)");
					lowerOrders.ForEach(order =>
					{
						var updated = order;

						if (!_botSettings.AllocationSettingsOn)
						{
							if (order.Limit < currentMarketSettings.MaxLimitSpeed)
								updated = this.SetLimit(order, currentMarketSettings.MaxLimitSpeed);
							this.SetPrice(updated, updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName]);
						}
						else
						{
							var allocationSettings = currentMarketSettings.AllocationSettings;
							if (order.Limit != allocationSettings.OtherOrdersLimitSettings)
								updated = this.SetLimit(order, allocationSettings.OtherOrdersLimitSettings);
							this.SetPrice(updated, updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName]);

						}
					});

					var upperOrders = myAlgMarketOrders.Where(x => !processedOrderIds.Contains(x.Id) && x.Price > targetPrice).ToList();
					Console.WriteLine($"\t[{algoKey}]\tПонижаем limit и speed активным ордерам выше главного ({upperOrders.Count} шт)");
					upperOrders.ForEach(order =>
					{
						var updated = order;
						if (order.Limit != this.MinLimitByAlgoritm[order.AlgorithmName])
							updated = this.SetLimit(order, this.MinLimitByAlgoritm[order.AlgorithmName]);
						this.SetPrice(updated, updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName]);
					});
				}
			}

			this.RunRefillLogic(myOrders);

			_iteration++;

			var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
			File.WriteAllText(metadataFileName, JsonConvert.SerializeObject(this.OrdersMetadataList));

			Console.WriteLine("\n***Окончание цикла***\n");
			_cycleIsActive = false;
		}



		private Order GetNextFreeOrder(string algoKey, BookOrder targetOrder, List<Order> myAlgMarketOrders, float targetPrice, float maxLimitSpeed)
		{
			Console.WriteLine($"\t[{algoKey}]\tПоиск ближайшего ордера снизу от конкурирующего");
			var myNextOrder = myAlgMarketOrders.FirstOrDefault(x => x.Price < targetOrder.Price);
			if (myNextOrder != null)
			{
				Console.WriteLine($"\t[{algoKey}]\tНайден ордер {myNextOrder.Id}. Устанавливаем цену {targetPrice} и скорость {maxLimitSpeed}");
				myNextOrder = _ac.updateOrder(algoKey, myNextOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), maxLimitSpeed.ToString(new CultureInfo("en-US")))?.Item1;
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

		private Order UpdateOrder(Order order, float price, float limit)
		{
			var updated = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
			return updated;
		}

		private Order SetLimit(Order order, float limit = 0)
		{
			//if (limit == 0)
			//	limit = this.MinLimitByAlgoritm[order.AlgorithmName];
			order = _ac.updateOrder(order.AlgorithmName, order.Id, order.Price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
			return order;
		}

		private Order SetPrice(Order order, float price)
		{
			var id = order.Id;
			if (price < order.Price)
			{
				var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
				var metadata = this.OrdersMetadataList.FirstOrDefault(x => x.Id == id);
				if (metadata == null)
				{
					metadata = new MyOrderMetadata { Id = order.Id, LastPriceDecreasedTime = 0 };
					this.OrdersMetadataList.Add(metadata);
				}
				var difference = currentTimeStamp - metadata.LastPriceDecreasedTime;
				if (difference > 600)
				{
					order = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), order.Limit.ToString(new CultureInfo("en-US")))?.Item1;
					if (order != null)
					{
						metadata.LastPriceDecreasedTime = currentTimeStamp;
					}
					return order;
				}
				else
					return null;
			}

			order = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), order.Limit.ToString(new CultureInfo("en-US")))?.Item1;
			return order;
		}

		private SlowDownResult SlowDownOrder(Order myOrder, float price, bool slowDownLimit = true)
		{
			// Понизить limit.
			if (slowDownLimit && myOrder.Limit > this.MinLimitByAlgoritm[myOrder.AlgorithmName])
				this.SetLimit(myOrder);

			var result = SlowDownResult.Undefined;

			var decreasedPrice = price + this.DownStepByAlgoritm[myOrder.AlgorithmName];
			if (price <= 0)
				throw new ApplicationException("Ошибка понижения цены");

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
				myOrder = _ac.updateOrder(myOrder.AlgorithmName, myOrder.Id, decreasedPrice.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
				if (myOrder != null)
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

		private List<Order> GetMyOrders()
		{
			var myOrders = new List<Order>();
			foreach (var jOrder in _orders)
			{
				var myOrder = JsonConvert.DeserializeObject<Order>(jOrder.ToString());
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

		private void RefreshOrders()
		{
			if (_ac.connected)
			{
				_orders = _ac.getOrders();
			}
		}

		private void WarnConsole(string message, bool critical = false)
		{
			Console.ForegroundColor = ConsoleColor.DarkYellow;
			if (critical)
				Console.ForegroundColor = ConsoleColor.DarkRed;
			Console.WriteLine(message);
			Console.ForegroundColor = ConsoleColor.White;
		}

		private void RunRefillLogic(List<Order> myOrders)
		{
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
		}

		private void FormOrdersGroup(List<Order> orders, List<string> processedOrderIds, string algoKey, BotMarketSettings currentMarketSettings, Order mainOrder)
		{
			if (_botSettings.AllocationSettingsOn)
			{
				var targetPrice = mainOrder.Price;
				orders = orders.Where(x => x.Id != mainOrder.Id).ToList();

				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine();
				Console.WriteLine($"\t[{algoKey}]\t***");
				Console.WriteLine($"\t[{algoKey}]\tФормирование группы ордеров после главного ордера");
				Console.ForegroundColor = ConsoleColor.White;

				var groupOrders = new List<Order>();

				var allocationSettings = currentMarketSettings.AllocationSettings;
				var minAllowedPrice = this.NormalizeFloat(targetPrice - (allocationSettings.ProcessedOrdersCount * allocationSettings.PriceStep));

				Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера: {targetPrice}. Минимально допустимая цена группы: {minAllowedPrice}");

				var ordersBetween = orders.Where(x => x.Price < targetPrice && x.Price >= minAllowedPrice).OrderByDescending(x => x.Price).ToList();

				Console.WriteLine($"\t[{algoKey}]\tКоличество ордеров в диапазоне: {ordersBetween.Count}");

				var targetPosition = 1;
				var previousOrderPrice = targetPrice;
				foreach (var order in ordersBetween)
				{
					var limitSetting = allocationSettings.LimitSettings.FirstOrDefault(x => x.OrdersPositionsList.Contains(targetPosition));

					var updated = order;

					var delta = previousOrderPrice - order.Price;
					if (delta > allocationSettings.PriceStep)
					{
						var newPrice = this.NormalizeFloat( previousOrderPrice - allocationSettings.PriceStep);
						var newLimit = order.Limit != limitSetting.MaxLimitSpeed ? limitSetting.MaxLimitSpeed : order.Limit;
						updated = this.UpdateOrder(order, newPrice, newLimit);
						if (updated == null)
						{
							Thread.Sleep(2500);
							updated = this.UpdateOrder(order, newPrice, newLimit);
						}
					}

					else if (delta <= 0.002f)
					{
						var previousOrder = groupOrders.LastOrDefault();
						if (previousOrder != null)
						{
							Thread.Sleep(2500);
							this.SetPrice(previousOrder, previousOrder.Price + 0.002f);
						}
						// todo change price
					}

					groupOrders.Add(updated);
					targetPosition = groupOrders.Count + 1;
					previousOrderPrice = updated.Price;

					if (updated.Limit != limitSetting.MaxLimitSpeed)
						this.SetLimit(order, limitSetting.MaxLimitSpeed);

					if (groupOrders.Count == allocationSettings.ProcessedOrdersCount) break;
				}

				groupOrders = groupOrders.Take(allocationSettings.ProcessedOrdersCount).ToList();

				if (groupOrders.Count < allocationSettings.ProcessedOrdersCount)
				{
					previousOrderPrice = groupOrders.Any() ? groupOrders.Last().Price : targetPrice;

					var lowerOrders = orders.Where(x => x.Price < previousOrderPrice && !processedOrderIds.Contains(x.Id)).ToList();

					foreach (var lowerOrder in lowerOrders)
					{
						targetPosition = groupOrders.Count + 1;
						var groupOrderTargetPrice = this.NormalizeFloat(previousOrderPrice - allocationSettings.PriceStep);
						var limitSetting = allocationSettings.LimitSettings.FirstOrDefault(x => x.OrdersPositionsList.Contains(targetPosition));

						var newPrice = groupOrderTargetPrice;
						var newLimit = lowerOrder.Limit != limitSetting.MaxLimitSpeed ? limitSetting.MaxLimitSpeed : lowerOrder.Limit;
						var updated = this.UpdateOrder(lowerOrder, newPrice, newLimit);
						if (updated != null)
						{
							groupOrders.Add(updated);
							previousOrderPrice = groupOrderTargetPrice;

						}

						if (groupOrders.Count == allocationSettings.ProcessedOrdersCount) break;
					}
				}

				processedOrderIds.AddRange(groupOrders.Select(x => x.Id));

				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine($"\t[{algoKey}]\tФормирование группы ордеров после главного ордера завершено\n");
				Console.ForegroundColor = ConsoleColor.White;
			}
		}

		private float NormalizeFloat(float value, int decimals = 4)
		{
			return (float)Math.Round(Convert.ToDouble(value, new CultureInfo("en-US")), decimals);
		}
	}

	public enum SlowDownResult
	{
		Undefined,
		Ok,
		ApiError
	}
}