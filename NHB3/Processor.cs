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
		private long _iteration = 1;
		private readonly List<string> _marketNames;
		private long lastRunStamp;

		private readonly ApiConnect _ac;
		private readonly BotSettings _botSettings;
		private JArray _orders;

		private readonly string _botId;

		private bool _settingsError;

		private string _currency = "TBTC";

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

			var myOrders = this.GetMyOrders();
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
				$"priceLimitToFindOrder: {_botSettings.PriceLimitToFindOrder}\n" +
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

			var acSettings = _ac.readSettings();
			if (acSettings.Enviorment == 1)
				_currency = "BTC";
			_ac.currency = _currency;
		}

		private void ValidateSettings()
		{
			Console.WriteLine("Проверка настроек");
			if (_botSettings.JsonPrice > 0)
				this.WarnConsole("Включено переопределение json цены в настройках. Для выключения установите параметр 'jsonPrice' равным 0");

			if (_botSettings.RunBotDelay == 0)
			{
				this.WarnConsole("Значение runBotDelay не может быть меньше или равно 0", true);
				_settingsError = true;
			}

			if (_botSettings.RunRefillDelay == 0)
			{
				this.WarnConsole("Значение runRefillDelay не может быть меньше или равно 0", true);
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
		internal bool CycleIsActive { get; set; }

		internal void SwitchState(bool running) => this.BotRunning = running;

		internal void SwitchErrorState(bool isErrorState) => this.IsErrorState = isErrorState;

		internal void SwicthCycle(bool running) => this.CycleIsActive = running;

		private Dictionary<string, string> MainOrdersByAlgoMarket { get; set; } = new Dictionary<string, string>();
		private Dictionary<string, long> LowerOrdersNextIterationRunByAlgoMarket { get; set; } = new Dictionary<string, long>();

		internal void RunBot()
		{
			// START
			if (!this.BotRunning || this.IsErrorState || this.CycleIsActive)
				return;

			if (_settingsError)
			{
				this.WarnConsole("Ошибка настроек, логика не будет запущена. Исправьте ошибки и перезапустите бот", true);
				return;
			}

			var balance = this.GetBalance();
			if (_botSettings.MinBalanceToRunBot > balance)
			{
				this.WarnConsole($"Баланс аккаунта ({balance}) меньше настройки {nameof(_botSettings.MinBalanceToRunBot)} ({_botSettings.MinBalanceToRunBot})", true);
				return;
			}

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"***Начало цикла {_iteration}***\n");
			this.CycleIsActive = true;

			this.RefreshOrders();

			var jAlgorithms = _ac.algorithms;

			var myOrders = this.GetMyOrders();

			var myAlgorithmNames = myOrders.Select(x => x.AlgorithmName).Distinct().ToList();
			var myMarketNames = myOrders.Select(x => x.MarketName).Distinct().ToList();
			var myOrderIds = myOrders.Select(x => x.Id).ToList();
			var allBookOrders = this.GetAllOrders(_marketNames, jAlgorithms, myAlgorithmNames, myMarketNames).Where(x => !myOrderIds.Contains(x.Id)).ToList();
			var jsonPrice = GetJsonPrice(_botSettings.JsonSettingsUrl);

			var fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
			var bs = JsonConvert.DeserializeObject<BotSettings>(File.ReadAllText(fileName));
			if (bs.JsonPrice != 0)
				jsonPrice = bs.JsonPrice;

			var marketSettings = _botSettings.MarketSettings;
			var marketSettingNames = marketSettings.Select(x => x.Name).ToList();

			var missingMarketNames = myMarketNames.Where(x1 => marketSettingNames.All(x2 => x2 != x1)).ToList();
			if (missingMarketNames.Count > 0)
				this.WarnConsole($"В настройках не найдены настройки для маркетов: {string.Join(", ", missingMarketNames)}. Маркеты не будут обработаны");

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

					var algoMarketKey = $"{algoKey}.{marketKey}";

					Console.WriteLine("\t***");
					Console.WriteLine($"\t[{algoKey}]\tОбработка маркета [{marketKey}]");

					var currentMarketSettings = marketSettings.FirstOrDefault(x => x.Name == marketKey);
					if (currentMarketSettings == null)
					{
						this.WarnConsole($"\t[{algoKey}]\tНе задана настройка маркета [{marketKey}]");
						continue;
					}

					var myAlgMarketOrders = myAlgMarketOrdersKVP.Value;
					var myAlgMarketOrdersIds = myAlgMarketOrders.Select(x => x.Id).ToList();
					var bookAlgMarketOrders = bookAlgOrdersByMarket[marketKey];

					var targetBookOrders = bookAlgMarketOrders
						.Where(x => x.Price < jsonPrice)
						.OrderByDescending(x => x.Price)
						.ToList();

					var targetOrder = currentMarketSettings.RivalOrderDetectionSettings != null && currentMarketSettings.RivalOrderDetectionSettings.On
						? GetTargetBookOrderByLimitRange(currentMarketSettings.RivalOrderDetectionSettings, targetBookOrders)
						: GetTargetBookOrderBySpeedLimits(totalLimit, targetBookOrders);
					if (targetOrder == null)
					{
						this.WarnConsole($"\t[{algoKey}]\tНе найден подходящий чужой ордер. Понижение скорости всем ордерам на маркете.");

						myAlgMarketOrders.ForEach(x =>
						{
							var limit = this.MinLimitByAlgoritm[x.AlgorithmName];
							this.SetLimit(x, limit);
						});

						continue;
					}
					Console.WriteLine($"\t[{algoKey}]\tЦена, скорость, id конкурирующего ордера: {targetOrder.Price} | {targetOrder.Limit} | {targetOrder.Id}");

					var newMainOrder = GetNewMainOrder(jsonPrice, processedOrderIds, algoKey, totalLimit, currentMarketSettings, myAlgMarketOrders, bookAlgMarketOrders, targetOrder);
					if (newMainOrder == null)
						continue;

					Console.WriteLine($"\t[{algoKey}]\tНаш текущий главный ордер имеет Id {newMainOrder.Id}");
					processedOrderIds.Add(newMainOrder.Id);

					if (!this.MainOrdersByAlgoMarket.ContainsKey(algoMarketKey))
						this.MainOrdersByAlgoMarket.Add(algoMarketKey, "");

					var mainOrderChanged = this.MainOrdersByAlgoMarket[algoMarketKey] != newMainOrder.Id;

					this.MainOrdersByAlgoMarket[algoMarketKey] = newMainOrder.Id;

					var needToRunLowerOrdersLogic = false;
					var lowerOrdersLogicJustRan = false;
					if (currentMarketSettings.LowerOrdersLogicRunCycleGap != 0)
					{
						if (!this.LowerOrdersNextIterationRunByAlgoMarket.ContainsKey(algoMarketKey))
							this.LowerOrdersNextIterationRunByAlgoMarket.Add(algoMarketKey, currentMarketSettings.LowerOrdersLogicRunCycleGap);

						needToRunLowerOrdersLogic = ((float)this.LowerOrdersNextIterationRunByAlgoMarket[algoMarketKey] / _iteration) == 1;
						if (needToRunLowerOrdersLogic)
						{
							Console.WriteLine($"\t[{algoKey}]\tЛогика понижения скорости");

							var minTargetPrice = newMainOrder.Price - currentMarketSettings.LowerOrdersLimitPriceRange;
							var minLimit = currentMarketSettings.LowerOrdersLimitThreshold;

							Console.WriteLine($"\t[{algoKey}]\tПоиск ордеров в диапазоне [{newMainOrder.Price}] - [{minTargetPrice}].");

							var lowerBookOrders = bookAlgMarketOrders.Where(x => x.Alive && x.Price < newMainOrder.Price && x.Price >= minTargetPrice && !myAlgMarketOrdersIds.Contains(x.Id)).ToList();
							var notMinimalLimitOrder = lowerBookOrders.FirstOrDefault(x => x.Limit != 0 && x.Limit > minLimit);
							var hasOrdersWithHigherLimit = notMinimalLimitOrder != null;

							if (!hasOrdersWithHigherLimit)
							{
								Console.WriteLine($"\t[{algoKey}]\tВсе чужие ордера в диапазоне имеют limit 0 или меньше минимальной. Выставляем минимальную скорость нашим ордерам");
								foreach (var order in myAlgMarketOrders.Where(x => x.Id != newMainOrder.Id && x.Price > 0.0001f))
								{
									if (order.Limit != this.MinLimitByAlgoritm[algoKey])
										this.SetLimit(order, this.MinLimitByAlgoritm[algoKey]);
								}
								Console.WriteLine($"\t[{algoKey}]\tОрдерам выставлена минимальная скорость");

								currentMarketSettings.LowerOrdersSlowedDown = true;
								lowerOrdersLogicJustRan = true;
							}
							else
							{
								Console.WriteLine($"\t[{algoKey}]\tВ диапазоне найден орден с ценой выше минимальной. Ордерам не будет выставлена минимальная скорость");
							}

							this.LowerOrdersNextIterationRunByAlgoMarket[algoMarketKey] = _iteration + currentMarketSettings.LowerOrdersLogicRunCycleGap;
						}
					}

					if (currentMarketSettings.LowerOrdersSlowedDown && mainOrderChanged)
					{
						Console.WriteLine("Главный ордер изменился. Не повышать скорость ордерам ниже.");
					}
					else if (!needToRunLowerOrdersLogic && !lowerOrdersLogicJustRan)
					{
						this.FormOrdersGroup(myAlgMarketOrders, processedOrderIds, algoKey, currentMarketSettings, newMainOrder);
						this.ProcessLowerOrders(processedOrderIds, algoKey, currentMarketSettings, myAlgMarketOrders, newMainOrder.Price);
						currentMarketSettings.LowerOrdersSlowedDown = false;
					}
					this.ProcessUpperOrder(processedOrderIds, algoKey, myAlgMarketOrders, newMainOrder.Price);
				}
			}

			this.RunRefillLogic(myOrders);

			_iteration++;

			var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
			File.WriteAllText(metadataFileName, JsonConvert.SerializeObject(this.OrdersMetadataList));

			Console.WriteLine("\n***Окончание цикла***\n");
			this.CycleIsActive = false;
		}

		private Order GetNewMainOrder(float jsonPrice, List<string> processedOrderIds, string algoKey, float totalLimit, BotMarketSettings currentMarketSettings, List<Order> myAlgMarketOrders, List<BookOrder> bookAlgMarketOrders, BookOrder targetOrder)
		{
			var priceRaiseStep = currentMarketSettings.PriceRaiseStep == 0 ? 0.0001f : currentMarketSettings.PriceRaiseStep;
			var targetPrice = (float)Math.Round(targetOrder.Price + priceRaiseStep, 4);

			Order newMainOrder = null;

			var mainOrderLimitSpeed = currentMarketSettings.MaxLimitSpeed;
			var myMainOrder = myAlgMarketOrders.OrderByDescending(x => x.Price).FirstOrDefault(x => x.Price >= targetOrder.Price && x.Price <= this.NormalizeFloat(targetPrice + priceRaiseStep, 4));
			if (myMainOrder != null)
			{
				mainOrderLimitSpeed = this.GetMainOrderLimit(algoKey, totalLimit, currentMarketSettings, myAlgMarketOrders, bookAlgMarketOrders, targetOrder.Price, mainOrderLimitSpeed, myMainOrder.Id);

				Console.WriteLine($"\t[{algoKey}]\tНаш главный ордер имеет цену {myMainOrder.Price}");
				if (myMainOrder.Price > targetOrder.Price && myMainOrder.Price > targetPrice)
				{
					Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера выше цены конкурирующего. Попытка снизить цену.");
					// Пытаемся снизить цену.
					var updated = this.UpdateOrder(myMainOrder, targetPrice, mainOrderLimitSpeed);
					if (updated != null)
					{
						Console.WriteLine($"\t[{algoKey}]\tЦена ордера установлена на {targetPrice}");
						newMainOrder = updated;
						processedOrderIds.Add(myMainOrder.Id);
					}

					// Не получилось снизить цену.
					else
					{
						newMainOrder = this.GetOrderInStepsRange(jsonPrice, algoKey, myAlgMarketOrders, targetOrder, targetPrice, totalLimit, currentMarketSettings, bookAlgMarketOrders);
					}
				}
				else if (myMainOrder.Price > targetOrder.Price && myMainOrder.Price <= targetPrice)
				{
					if (myMainOrder.Limit != mainOrderLimitSpeed)
						_ac.updateOrder(algoKey, myMainOrder.Id, myMainOrder.Price.ToString(new CultureInfo("en-US")), mainOrderLimitSpeed.ToString(new CultureInfo("en-US")));
					Console.WriteLine($"\t[{algoKey}]\tГлавный ордер стоит перед конкурирующим. Изменения не требуются.");
					newMainOrder = myMainOrder;
				}
				else
				{
					Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера ниже цены конкурирующего + шаг повышения ({priceRaiseStep}). Повышение цены.");
					myMainOrder = _ac.updateOrder(algoKey, myMainOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), mainOrderLimitSpeed.ToString(new CultureInfo("en-US")))?.Item1;
					if (myMainOrder != null)
					{
						Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена до {targetPrice}");
						newMainOrder = myMainOrder;
					}
					else
						Console.WriteLine($"\t[{algoKey}]\tОшибка при повышении цены");
				}
			}
			else
			{
				newMainOrder = this.GetOrderInStepsRange(jsonPrice, algoKey, myAlgMarketOrders, targetOrder, targetPrice, totalLimit, currentMarketSettings, bookAlgMarketOrders);
			}

			if (newMainOrder == null)
				newMainOrder = this.GetNextFreeOrder(algoKey, myAlgMarketOrders, targetPrice, currentMarketSettings.MaxLimitSpeed, totalLimit, currentMarketSettings, bookAlgMarketOrders, targetOrder.Price);
			if (newMainOrder == null)
			{
				this.WarnConsole($"\t[{algoKey}]\tНе найден подходящий ордер с ценой ниже цены JSON. Понижение скорости и цены всем ордерам с ценой выше {targetPrice}");
				var uppers = myAlgMarketOrders.Where(x => x.Price > targetPrice).ToList();

				uppers.ForEach(x =>
				{
					var price = x.Price + this.DownStepByAlgoritm[x.AlgorithmName];
					var limit = this.MinLimitByAlgoritm[x.AlgorithmName];
					this.UpdateOrder(x, price, limit);
				});
			}

			return newMainOrder;
		}

		private static BookOrder GetTargetBookOrderByLimitRange(RivalOrderDetectionSettings settings, List<BookOrder> targetBookOrders)
		{
			BookOrder order = null;
			if (settings.MinLimit > 0 && settings.MaxLimit == 0)
				order = targetBookOrders.FirstOrDefault(x => x.Limit > settings.MinLimit || x.Limit == 0.0f);
			else if (settings.MinLimit == 0.0f && settings.MaxLimit == 0.0f)
				order = targetBookOrders.FirstOrDefault(x => x.Limit == 0.0f);
			else if (settings.MinLimit > 0 && settings.MaxLimit > 0)
				order = targetBookOrders.FirstOrDefault(x => x.Limit > settings.MinLimit && x.Limit < settings.MaxLimit);

			return order;
		}

		private Order GetOrderInStepsRange(float jsonPrice, string algoKey, List<Order> myAlgMarketOrders, BookOrder targetOrder, float targetPrice, float totalLimit, BotMarketSettings currentMarketSettings, List<BookOrder> bookAlgMarketOrders)
		{
			var priceLimit = _botSettings.PriceLimitToFindOrder == 0.0f
				? targetPrice + Math.Abs(this.DownStepByAlgoritm[algoKey]) * _botSettings.MinStepsCountToFindOrder
				: targetPrice + _botSettings.PriceLimitToFindOrder;

			var myNextUpperOrder =
				myAlgMarketOrders
				.OrderByDescending(x => x.Price)
				.FirstOrDefault(x => x.Price >= targetOrder.Price && x.Price < priceLimit && x.Price < jsonPrice);

			if (myNextUpperOrder != null)
			{
				var mainOrderLimitSpeed = this.GetMainOrderLimit(algoKey, totalLimit, currentMarketSettings, myAlgMarketOrders, bookAlgMarketOrders, targetOrder.Price, myNextUpperOrder.Limit, myNextUpperOrder.Id);

				Console.WriteLine($"\t[{algoKey}]\tНайден ордер с ценой {myNextUpperOrder.Price}");

				var newPrice = myNextUpperOrder.Price > targetPrice
					? this.NormalizeFloat(myNextUpperOrder.Price + this.DownStepByAlgoritm[algoKey])
					: myNextUpperOrder.Price;
				if (newPrice < targetPrice)
					newPrice = targetPrice;

				var newLimit = mainOrderLimitSpeed;

				if (!CompareFloats(newLimit, myNextUpperOrder.Limit, 4) || !CompareFloats(newPrice, myNextUpperOrder.Price, 4))
				{
					Console.WriteLine($"\t[{algoKey}]\tУстановка ордеру цены {newPrice} и скорости {newLimit}");
					myNextUpperOrder = this.UpdateOrder(myNextUpperOrder, newPrice, newLimit);
				}
				return myNextUpperOrder;
			}
			else
			{
				Console.WriteLine($"\t[{algoKey}]\tОрдер в пределах {_botSettings.MinStepsCountToFindOrder} минимальных шагов / лимита цены {_botSettings.PriceLimitToFindOrder} не найден.");
				return null;
			}
		}

		private void ProcessUpperOrder(List<string> processedOrderIds, string algoKey, List<Order> myAlgMarketOrders, float targetPrice)
		{
			var upperOrders = myAlgMarketOrders.Where(x => !processedOrderIds.Contains(x.Id) && x.Price > targetPrice).ToList();
			Console.WriteLine($"\t[{algoKey}]\tОбработка ордеров выше главного ({upperOrders.Count} шт)");
			upperOrders.ForEach(order =>
			{
				var updated = order;
				if (order.Limit != this.MinLimitByAlgoritm[order.AlgorithmName])
					updated = this.SetLimit(order, this.MinLimitByAlgoritm[order.AlgorithmName]);
				this.SetPrice(updated, updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName]);
			});
		}

		private void ProcessLowerOrders(List<string> processedOrderIds, string algoKey, BotMarketSettings currentMarketSettings, List<Order> myAlgMarketOrders, float targetPrice)
		{
			var lowerOrders = myAlgMarketOrders.Where(x => !processedOrderIds.Contains(x.Id) && x.Price <= targetPrice && x.Price > 0.0001f).ToList();
			Console.WriteLine($"\t[{algoKey}]\tОбработка ордеров ниже главного ({lowerOrders.Count} шт)");
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
		}

		private float GetMainOrderLimit(string algoKey, float totalLimit, BotMarketSettings currentMarketSettings, List<Order> myAlgMarketOrders, List<BookOrder> bookAlgMarketOrders, float targetOrderPrice, float mainOrderLimitSpeed, string mainOrderId)
		{
			if (currentMarketSettings.MaxLimitSpeedPercent > 0)
			{
				Console.WriteLine($"\t[{algoKey}]\tВключена настройка переопределения скорости для главного ордера");

				var upperOrdersPayingSpeedSum = bookAlgMarketOrders.Where(x => x.Alive && x.Price > targetOrderPrice).Sum(x => x.Limit);
				upperOrdersPayingSpeedSum += myAlgMarketOrders.Where(x => x.Id != mainOrderId && x.Price > targetOrderPrice).Sum(x => x.Limit);

				var ratio = this.NormalizeFloat(currentMarketSettings.MaxLimitSpeedPercent / 100 * totalLimit, 2);
				var calculatedLimit = this.NormalizeFloat(ratio - upperOrdersPayingSpeedSum);

				var minLimitByAlgoString = this.MinLimitByAlgoritm[algoKey].ToString();
				var parts = minLimitByAlgoString.Split('.');
				var decimals = parts[1].Length;

				calculatedLimit = this.NormalizeFloat(calculatedLimit, decimals);

				Console.WriteLine($"\t[{algoKey}]\tОбщая скорость маркета: {totalLimit}. Сумма лимитов ордеров выше главного: {upperOrdersPayingSpeedSum}. Процент от маркета из настройки: {ratio}.");

				Console.WriteLine($"\t[{algoKey}]\tРассчитанный лимит с учётом шага - [{calculatedLimit}]");

				if (calculatedLimit <= 0)
				{
					Console.WriteLine($"\t[{algoKey}]\tРассчитанный лимит меньше 0. Настройка не будет учитываться");
				}
				else
				{
					mainOrderLimitSpeed = calculatedLimit;
				}
			}
			else
				mainOrderLimitSpeed = currentMarketSettings.MaxLimitSpeed;

			return mainOrderLimitSpeed;
		}

		private Order GetNextFreeOrder(string algoKey, List<Order> myAlgMarketOrders, float targetPrice, float maxLimitSpeed, float totalLimit, BotMarketSettings currentMarketSettings, List<BookOrder> bookAlgMarketOrders, float targetOrderPrice)
		{
			Console.WriteLine($"\t[{algoKey}]\tПоиск ближайшего ордера снизу от конкурирующего");
			var myNextOrder = myAlgMarketOrders.FirstOrDefault(x => x.Price < targetPrice);
			if (myNextOrder != null)
			{
				var mainOrderLimitSpeed = this.GetMainOrderLimit(algoKey, totalLimit, currentMarketSettings, myAlgMarketOrders, bookAlgMarketOrders, targetOrderPrice, maxLimitSpeed, myNextOrder.Id);

				Console.WriteLine($"\t[{algoKey}]\tНайден ордер {myNextOrder.Id}. Устанавливаем цену {targetPrice} и скорость {mainOrderLimitSpeed}");
				myNextOrder = _ac.updateOrder(algoKey, myNextOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), mainOrderLimitSpeed.ToString(new CultureInfo("en-US")))?.Item1;
				return myNextOrder;
			}
			else
			{
				this.WarnConsole($"\t[{algoKey}]\tНе осталось свободных ордеров для повышения цены");
				return null;
			}
		}

		private static BookOrder GetTargetBookOrderBySpeedLimits(float totalLimit, List<BookOrder> targetBookOrders)
		{
			var currentLimit = 0.0f;
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
				if (difference > 605)
				{
					order = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
					metadata.LastPriceDecreasedTime = currentTimeStamp;
					return order ?? this.SetLimit(order, limit);
				}
				else
					return this.SetLimit(order, limit);
			}
			else
			{
				var updated = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
				return updated;
			}
		}

		private Order SetLimit(Order order, float limit = 0)
		{
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
				if (difference > 605)
				{
					order = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), order.Limit.ToString(new CultureInfo("en-US")))?.Item1;
					if (order != null)
					{
					}

					metadata.LastPriceDecreasedTime = currentTimeStamp;
					return order;
				}
				else
					return null;
			}

			order = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), order.Limit.ToString(new CultureInfo("en-US")))?.Item1;
			return order;
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

			if (_iteration == 1)
				lastRunStamp = currentTimeStamp;

			if ((currentTimeStamp - lastRunStamp) >= _botSettings.RunRefillDelay)
			{
				Console.WriteLine("\nRefill orders start");

				myOrders.ForEach(order =>
				{
					var ok = ( (order.PayedAmount > _botSettings.RefillPayedAmountLimit) && ((order.AvailableAmount - order.PayedAmount) < _botSettings.RefillOrderLimit));
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
				var allocationSettings = currentMarketSettings.AllocationSettings;

				if (allocationSettings.ProcessedOrdersCount <= 0) return;

				var targetPrice = mainOrder.Price;
				orders = orders.Where(x => x.Id != mainOrder.Id).ToList();

				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine();
				Console.WriteLine($"\t[{algoKey}]\t***");
				Console.WriteLine($"\t[{algoKey}]\tФормирование группы ордеров после главного ордера");
				Console.ForegroundColor = ConsoleColor.White;

				var groupOrders = new List<Order>();

				var allocationSettingsSum = allocationSettings.LimitSettings.Take(allocationSettings.ProcessedOrdersCount).Sum(x => x.PriceStep);

				var minAllowedPrice = this.NormalizeFloat(targetPrice - allocationSettingsSum);

				Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера: {targetPrice}. Минимально допустимая цена группы: {minAllowedPrice}");

				var ordersBetween = orders.Where(x => x.Id != mainOrder.Id && x.Price <= targetPrice && x.Price >= minAllowedPrice).OrderByDescending(x => x.Price).ToList();

				Console.WriteLine($"\t[{algoKey}]\tКоличество ордеров в диапазоне [{targetPrice}]-[{minAllowedPrice}] : {ordersBetween.Count}");

				var targetPosition = 1;
				//var previousOrderPrice = targetPrice;
				foreach (var order in ordersBetween)
				{
					Console.WriteLine($"\t[{algoKey}]\tОбработка ордера с Id [{order.Id}]. Цена: [{order.Price}]. Цена предыдущего ордера: [{targetPrice}]");

					var limitSetting = allocationSettings.LimitSettings.FirstOrDefault(x => x.OrdersPositionsList.Contains(targetPosition));

					var updated = order;

					var delta = this.NormalizeFloat(targetPrice - order.Price);
					if (delta > limitSetting.PriceStep)
					{
						var newPrice = this.NormalizeFloat(targetPrice - limitSetting.PriceStep);
						var newLimit = order.Limit != limitSetting.MaxLimitSpeed ? limitSetting.MaxLimitSpeed : order.Limit;

						var floatsEqual = CompareFloats(newPrice, order.Price, 4);
						if (!floatsEqual)
						{
							Console.WriteLine($"\t[{algoKey}]\tДельта с ценой предыдущего ордера выше {limitSetting.PriceStep}. Повышаем цену до {newPrice}, изменяем скорость на {newLimit}");

							updated = this.UpdateOrder(order, newPrice, newLimit);
							if (updated == null)
							{
								Thread.Sleep(2500);
								updated = this.UpdateOrder(order, newPrice, newLimit);
							}
						}
					}
					else if (delta < limitSetting.PriceStep)
					{
						var desiredPrice = this.NormalizeFloat(targetPrice - limitSetting.PriceStep, 4);
						var step = this.DownStepByAlgoritm[order.AlgorithmName];

						if (this.NormalizeFloat(order.Price + step, 4) > desiredPrice)
						{
							this.SetPrice(order, order.Price + step);
						}
						else
							this.SetPrice(order, desiredPrice);
					}

					groupOrders.Add(updated);
					targetPosition = groupOrders.Count + 1;
					//previousOrderPrice = updated.Price;

					if (updated.Limit != limitSetting.MaxLimitSpeed)
						this.SetLimit(order, limitSetting.MaxLimitSpeed);

					if (groupOrders.Count == allocationSettings.ProcessedOrdersCount) break;
				}

				groupOrders = groupOrders.Take(allocationSettings.ProcessedOrdersCount).ToList();
				Console.WriteLine($"\t[{algoKey}]\tЗакончена обработка ордеров в диапазоне [{targetPrice}]-[{minAllowedPrice}]");

				processedOrderIds.AddRange(groupOrders.Select(x => x.Id));

				if (groupOrders.Count < allocationSettings.ProcessedOrdersCount)
				{
					Console.WriteLine($"\t[{algoKey}]\tКоличество ордеров в группе главных меньше {allocationSettings.ProcessedOrdersCount}. Поиск ордеров с меньшей стоимостью.");

					//previousOrderPrice = groupOrders.Any() ? groupOrders.Last().Price : targetPrice;

					var lowerOrders = orders.Where(x => x.Price <= targetPrice && !processedOrderIds.Contains(x.Id)).OrderByDescending(x => x.Price).ToList();
					Console.WriteLine($"\t[{algoKey}]\tКоличество найденных ордеров с меньшей стоимостью: {lowerOrders.Count}");

					foreach (var lowerOrder in lowerOrders)
					{
						targetPosition = groupOrders.Count + 1;
						var limitSetting = allocationSettings.LimitSettings.FirstOrDefault(x => x.OrdersPositionsList.Contains(targetPosition));
						var groupOrderTargetPrice = this.NormalizeFloat(targetPrice - limitSetting.PriceStep);

						var newPrice = groupOrderTargetPrice;
						var newLimit = lowerOrder.Limit != limitSetting.MaxLimitSpeed ? limitSetting.MaxLimitSpeed : lowerOrder.Limit;

						var pricesEqual = CompareFloats(lowerOrder.Price, newPrice, 4);

						if (!pricesEqual)
						{
							Console.WriteLine($"\t[{algoKey}]\tОбработка ордера с Id [{lowerOrder.Id}] и ценой [{lowerOrder.Price}]. Установка цены {newPrice} скорости {newLimit}");

							var updated = this.UpdateOrder(lowerOrder, newPrice, newLimit);
							if (updated != null)
							{
								groupOrders.Add(updated);
								//previousOrderPrice = groupOrderTargetPrice;
							}
							else
								this.WarnConsole($"\t[{algoKey}]\tНе удалось обработать ордер с Id [{lowerOrder.Id}]");
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

		private float GetBalance()
		{
			var result = 0.0f;
			if (_ac.connected)
			{
				JObject balance = _ac.getBalance(_currency);
				if (balance != null)
				{
					result = float.Parse(balance["available"].ToString(), CultureInfo.InvariantCulture);
				}
			}
			return result;
		}

		private float NormalizeFloat(float value, int decimals = 4)
		{
			return (float)Math.Round(Convert.ToDouble(value, new CultureInfo("en-US")), decimals);
		}

		public static unsafe int FloatToInt32Bits(float f)
		{
			return *((int*)&f);
		}

		public static bool CompareFloats(float a, float b, int maxDeltaBits)
		{
			int aInt = FloatToInt32Bits(a);
			if (aInt < 0)
				aInt = Int32.MinValue - aInt;

			int bInt = FloatToInt32Bits(b);
			if (bInt < 0)
				bInt = Int32.MinValue - bInt;

			int intDiff = Math.Abs(aInt - bInt);
			return intDiff <= (1 << maxDeltaBits);
		}

	}
}