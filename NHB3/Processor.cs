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
		private long lastRefillRunStamp;
		private long lastBalanceCheckRunStamp;
		private long lastCancellationRunStamp;

		private readonly ApiConnect _ac;
		private readonly Settings _settings;
		private MarketSettings _currentMarketSettings;
		private JArray _orders;

		private readonly string _botId;

		private bool _settingsError;

		private string _currency = "TBTC";

		private List<Pool> _myPools = new List<Pool>();

		private List<Order> _activeOrders = new List<Order>();

		public Processor(ApiConnect ac, Settings settings, JArray orders, string botId)
		{
			_ac = ac ?? throw new ArgumentNullException(nameof(ac));
			_settings = settings ?? throw new ArgumentNullException(nameof(settings));
			_orders = orders ?? throw new ArgumentNullException(nameof(orders));
			_botId = botId ?? throw new ArgumentNullException(nameof(botId));

			_marketNames = _ac.getMarkets();

			this.Initialize();
		}

		private void Initialize()
		{
			var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
			this.OrdersMetadataList = JsonConvert.DeserializeObject<List<MyOrderMetadata>>(File.ReadAllText(metadataFileName));

			this.RefreshOrders();
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
			_ac.getPools(true, _settings.BotSettings.AlgorithmName);
			_myPools = this.GetPools();
		}

		private void ValidateSettings()
		{
			Console.WriteLine("Проверка настроек");
			if (_settings.BotSettings.JsonPrice > 0)
				this.WarnConsole("Включено переопределение json цены в настройках. Для выключения установите параметр 'jsonPrice' равным 0");

			if (_settings.BotSettings.RunBotDelay == 0)
			{
				this.WarnConsole("Значение runBotDelay не может быть меньше или равно 0", true);
				_settingsError = true;
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

		private bool _balanceCheckPassed = true;

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

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"***Начало цикла {_iteration}***\n");
			this.CycleIsActive = true;

			this.RefreshOrders();

			var myOrders = this.GetMyOrders();
			CheckBalance(myOrders);
			if (!_balanceCheckPassed) return;

			var myMarketNames = myOrders.Select(x => x.MarketName).Distinct().ToList();
			var myOrderIds = myOrders.Select(x => x.Id).ToList();

			var allBookOrders = this.GetAllOrders(_marketNames, myMarketNames).Where(x => !myOrderIds.Contains(x.Id)).ToList();
			var jsonPrice = GetJsonPrice(_settings.BotSettings.JsonSettingsUrl);

			var fileName = Path.Combine(Directory.GetCurrentDirectory(), "bot.json");
			var bs = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(fileName));
			if (bs.BotSettings.JsonPrice != 0)
				jsonPrice = bs.BotSettings.JsonPrice;

			var marketSettings = _settings.MarketSettings;
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
					_activeOrders.Clear();
					var marketKey = myAlgMarketOrdersKVP.Key;
					var totalLimit = this.TotalSpeedByMarket[$"{algoKey}.{marketKey}"];

					_currentMarketSettings = _settings.MarketSettings.First(x => x.Name == marketKey);
					if (_currentMarketSettings.OrdersSettings.LimitFunction != "summaryCurrentSpeed")
					{
						this.WarnConsole("Для расчёта лимита сейчас поддерживается только функция 'summaryCurrentSpeed'", true);
						continue;
					}

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
					_activeOrders.Add(newMainOrder);

					Console.WriteLine($"\t[{algoKey}]\tНаш текущий главный ордер имеет Id {newMainOrder.Id}");
					processedOrderIds.Add(newMainOrder.Id);

					if (!this.MainOrdersByAlgoMarket.ContainsKey(algoMarketKey))
						this.MainOrdersByAlgoMarket.Add(algoMarketKey, "");

					var mainOrderChanged = this.MainOrdersByAlgoMarket[algoMarketKey] != newMainOrder.Id;

					this.MainOrdersByAlgoMarket[algoMarketKey] = newMainOrder.Id;

					var needToRunLowerOrdersLogic = false;
					var lowerOrdersLogicJustRan = false;
					if (currentMarketSettings.LowerOrdersSettings.LowerOrdersLogicRunCycleGap != 0)
					{
						if (!this.LowerOrdersNextIterationRunByAlgoMarket.ContainsKey(algoMarketKey))
							this.LowerOrdersNextIterationRunByAlgoMarket.Add(algoMarketKey, currentMarketSettings.LowerOrdersSettings.LowerOrdersLogicRunCycleGap);

						needToRunLowerOrdersLogic = ((float)this.LowerOrdersNextIterationRunByAlgoMarket[algoMarketKey] / _iteration) == 1;
						if (needToRunLowerOrdersLogic)
						{
							Console.WriteLine($"\t[{algoKey}]\tЛогика понижения скорости");

							var minTargetPrice = newMainOrder.Price - currentMarketSettings.LowerOrdersSettings.LowerOrdersLimitPriceRange;
							var minLimit = currentMarketSettings.LowerOrdersSettings.LowerOrdersLimitThreshold;

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

							this.LowerOrdersNextIterationRunByAlgoMarket[algoMarketKey] = _iteration + currentMarketSettings.LowerOrdersSettings.LowerOrdersLogicRunCycleGap;
						}
					}

					if (currentMarketSettings.LowerOrdersSlowedDown && mainOrderChanged)
					{
						Console.WriteLine("Главный ордер изменился. Не повышать скорость ордерам ниже.");
						_activeOrders.ForEach(x => this.UpdateOrder(x, x.Price, x.Limit));
					}
					else if (!needToRunLowerOrdersLogic && !lowerOrdersLogicJustRan)
					{
						this.FormOrdersGroup(myAlgMarketOrders, processedOrderIds, algoKey, currentMarketSettings, newMainOrder);

						var allocationSettings = _currentMarketSettings.OrdersSettings.AllocationSettings;
						switch (_currentMarketSettings.OrdersSettings.LimitFunction)
						{
							case "summaryCurrentSpeed":
								{
									var minLimit = this.MinLimitByAlgoritm[_settings.BotSettings.AlgorithmName];
									for (var i = 0; i < _activeOrders.Count; i++)
									{
										var activeOrder = _activeOrders[i];
										if (_iteration == 1)
										{
											if (!CompareFloats(activeOrder.OldPrice, activeOrder.Price, 4) || !CompareFloats(activeOrder.Limit, 1, 4))
												this.PatchOrder(activeOrder, 1);
										}
											//this.SetLimit(activeOrder, 1);
										else
										{
											var restOrders = _activeOrders.Skip(i+1).Where(x => x.RigsCount > 0);
											var speedSum = restOrders.Sum(x => x.AcceptedCurrentSpeed);
											var delta = _currentMarketSettings.OrdersSettings.SummaryCurrentSpeed - speedSum;
											var limit = delta > minLimit
												? delta
												: minLimit;

											if (!CompareFloats(activeOrder.OldPrice, activeOrder.Price, 4) || !CompareFloats(activeOrder.Limit, limit, 4))
												this.PatchOrder(activeOrder, limit);
											//this.SetLimit(activeOrder, limit);
										}
									}
									break;
								}
							default:
								throw new NotImplementedException();
						}



						this.ProcessLowerOrders(processedOrderIds, algoKey, currentMarketSettings, myAlgMarketOrders, newMainOrder.Price);
						currentMarketSettings.LowerOrdersSlowedDown = false;
					}
					this.ProcessUpperOrder(processedOrderIds, algoKey, myAlgMarketOrders, newMainOrder.Price);

					this.CheckEmptyOrders(marketKey, currentMarketSettings);
				}
			}

			this.RunRefillLogic(myOrders);
			this.CheckOrdersForCancellation(jsonPrice);

			this.EndCycle();
		}

		private void EndCycle()
		{
			_iteration++;

			var metadataFileName = Path.Combine(Directory.GetCurrentDirectory(), "ordersList.json");
			File.WriteAllText(metadataFileName, JsonConvert.SerializeObject(this.OrdersMetadataList));

			Console.WriteLine("\n***Окончание цикла***\n");
			this.CycleIsActive = false;
		}

		private void CheckEmptyOrders(string marketName, MarketSettings marketSettings)
		{
			Console.WriteLine("Проверка пустых оредров");
			var emptyOrderSettings = marketSettings.EmptyOrderSettings;

			this.RefreshOrders();
			var allCurrentOrders = this.GetMyOrders();
			var currentMarketOrders = allCurrentOrders.Where(x => x.MarketName == marketName).ToList();

			var alg = _ac.algorithms.FirstOrDefault(x => x["algorithm"].ToString() == _settings.BotSettings.AlgorithmName);
			var minPrice = float.Parse(alg["minimalOrderAmount"].ToString(), CultureInfo.InvariantCulture);
			var minLimit = this.MinLimitByAlgoritm[_settings.BotSettings.AlgorithmName];
			var minAmount = minPrice;

			if (emptyOrderSettings.Price != 0)
				minPrice = emptyOrderSettings.Price;
			if (emptyOrderSettings.Limit != 0)
				minLimit = emptyOrderSettings.Limit;
			if (emptyOrderSettings.Amount != 0)
				minAmount = emptyOrderSettings.Amount;

			var currentEmptyOrders = currentMarketOrders.Where(x => CompareFloats(x.Price, minPrice, 4)).ToList();
			var delta = emptyOrderSettings.Quantity - currentEmptyOrders.Count;

			Console.WriteLine($"Текущие пустые ордера: {currentEmptyOrders.Count}. Настройка {nameof(emptyOrderSettings.Quantity)}: {emptyOrderSettings.Quantity}. Разница: {delta}");

			if (delta > 0)
			{
				if (!emptyOrderSettings.PoolNameOrders)
				{
					var targetPoolName = emptyOrderSettings.PoolNameOrdersStart;
					var targetPool = _myPools.OrderBy(x => int.Parse(x.Name)).FirstOrDefault(x => int.Parse(x.Name) >= targetPoolName);
					Console.WriteLine($"{nameof(emptyOrderSettings.PoolNameOrders)} - false, новые ордера будут созданы в пуле {targetPool.Name}");
					for (var i = 0; i < delta; i++)
					{
						_ac.createOrder(_settings.BotSettings.AlgorithmName,
							marketName,
							"STANDARD",
							targetPool.Id.ToString(),
							minPrice.ToString(new CultureInfo("en-US")),
							minLimit.ToString(new CultureInfo("en-US")),
							minAmount.ToString(new CultureInfo("en-US")));
					}
				}
				else
				{
					var currentPools = allCurrentOrders.Select(x => x.PoolName).ToList();
					var freePools = _myPools.Where(x => !currentPools.Contains(x.Name)).OrderBy(x => int.Parse(x.Name));

					var range = new List<int>();
					for (int i = emptyOrderSettings.PoolNameOrdersStart; i <= emptyOrderSettings.PoolNameOrdersEnd; i++)
					{
						range.Add(i);
					}

					//var poolsCount = emptyOrderSettings.PoolNameOrdersEnd - emptyOrderSettings.PoolNameOrdersStart + 1;
					//var targetPoolNames = Enumerable.Range(emptyOrderSettings.PoolNameOrdersStart, poolsCount);
					var targetPools = freePools.Where(x => range.Contains(int.Parse(x.Name))).OrderBy(x => int.Parse(x.Name)).ToList();

					Console.WriteLine($"{nameof(emptyOrderSettings.PoolNameOrders)} - true");

					for (var i = 0; i < delta; i++)
					{
						var pool = targetPools.FirstOrDefault();
						if (pool == null)
						{
							this.WarnConsole("Не осталось свободных пулов");
							return;
						}

						_ac.createOrder(_settings.BotSettings.AlgorithmName,
							marketName,
							"STANDARD",
							pool.Id.ToString(),
							minPrice.ToString(new CultureInfo("en-US")),
							minLimit.ToString(new CultureInfo("en-US")),
							minAmount.ToString(new CultureInfo("en-US")));
						targetPools.Remove(pool);
					}
				}
			}
		}

		private void CheckBalance(List<Order> myOrders)
		{
			var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
			if ((currentTimeStamp - lastBalanceCheckRunStamp) >= _settings.BotSettings.MinBalanceCheckInterval)
			{
				var balance = this.GetBalance();
				Console.WriteLine("Проверка баланса");
				if (_settings.BotSettings.MinBalanceToRunBot > balance)
				{
					var alg = _ac.algorithms.FirstOrDefault(x => x["algorithm"].ToString() == _settings.BotSettings.AlgorithmName);
					var minPrice = float.Parse(alg["minimalOrderAmount"].ToString(), CultureInfo.InvariantCulture);

					this.WarnConsole($"Баланс аккаунта ({balance}) меньше настройки {nameof(_settings.BotSettings.MinBalanceToRunBot)} ({_settings.BotSettings.MinBalanceToRunBot}). Отправка команды на снижение цены и скорости", true);
					myOrders.ForEach(x =>
					{
						if (x.Price == minPrice || x.Price + this.DownStepByAlgoritm[x.AlgorithmName] < minPrice)
						{
							this.SetLimit(x, this.MinLimitByAlgoritm[x.AlgorithmName]);
						}
						else
						{
							var price = this.NormalizeFloat(x.Price + this.DownStepByAlgoritm[x.AlgorithmName], 4);
							var limit = this.MinLimitByAlgoritm[x.AlgorithmName];
							this.UpdateOrder(x, price, limit);
						}
					});
					this.WarnConsole("Работа завершена", true);
					_balanceCheckPassed = false;
					this.EndCycle();
				}
				else
				{
					Console.WriteLine($"Баланс аккаунта ({balance}) больше настройки {nameof(_settings.BotSettings.MinBalanceToRunBot)} ({_settings.BotSettings.MinBalanceToRunBot}). Продолжение работы.");
					_balanceCheckPassed = true;
					this.CycleIsActive = false;
				}
				lastBalanceCheckRunStamp = currentTimeStamp;
			}
			else if (!_balanceCheckPassed)
				this.EndCycle();
		}

		private void CheckOrdersForCancellation(float jsonPrice)
		{
			if (!_settings.CancellationSettings.On) return;
			var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
			if ((currentTimeStamp - lastCancellationRunStamp) >= _settings.CancellationSettings.Interval)
			{
				Console.WriteLine("Проверка ордеров с большой ценой.");
				this.RefreshOrders();
				var orders = this.GetMyOrders()
					.Where(x => x.Price > jsonPrice + _settings.CancellationSettings.JsonPriceExcessThreshold && x.AcceptedCurrentSpeed < _settings.CancellationSettings.AcceptedCurrentSpeedThreshold)
					.ToList();

				Console.WriteLine($"Количество найденных ордеров с большой ценой: {orders.Count}");

				orders.ForEach(x =>
				{
					Console.WriteLine($"Отмена ордера {x.Id}");
					_ac.cancelOrder(x.Id);
				});

				lastCancellationRunStamp = currentTimeStamp;
			}
		}

		private Order GetNewMainOrder(float jsonPrice, List<string> processedOrderIds, string algoKey, float totalLimit, MarketSettings currentMarketSettings, List<Order> myAlgMarketOrders, List<BookOrder> bookAlgMarketOrders, BookOrder targetOrder)
		{

			var temp = _currentMarketSettings.OrdersSettings.AllocationSettings.First();

			var priceRaiseStep = temp.PriceRaiseStep == 0 ? 0.0001f : temp.PriceRaiseStep;
			var targetPrice = (float)Math.Round(targetOrder.Price + priceRaiseStep, 4);

			Order newMainOrder = null;

			//var mainOrderLimitSpeed = currentMarketSettings.MaxLimitSpeed;
			var myMainOrder = myAlgMarketOrders.OrderByDescending(x => x.Price).FirstOrDefault(x => x.Price >= targetOrder.Price && x.Price <= this.NormalizeFloat(targetPrice, 4));
			if (myMainOrder != null)
			{
				//mainOrderLimitSpeed = this.GetMainOrderLimit(algoKey, totalLimit, currentMarketSettings, myAlgMarketOrders, bookAlgMarketOrders, targetOrder.Price, mainOrderLimitSpeed, myMainOrder.Id);

				Console.WriteLine($"\t[{algoKey}]\tНаш главный ордер имеет цену {myMainOrder.Price}");
				if (myMainOrder.Price > targetPrice)
				{
					//Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера выше цены конкурирующего. Попытка снизить цену.");
					// Пытаемся снизить цену.

					if (CompareFloats(myMainOrder.Price, (targetPrice + this.DownStepByAlgoritm[myMainOrder.AlgorithmName]), 4))
					{
						myMainOrder.Price = targetPrice;
					}

					//var updated = this.UpdateOrder(myMainOrder, targetPrice, myMainOrder.Limit);
					//if (updated != null)
					//{
					//	Console.WriteLine($"\t[{algoKey}]\tЦена ордера установлена на {targetPrice}");
					//	newMainOrder = updated;
					//	processedOrderIds.Add(myMainOrder.Id);
					//}

					// Не получилось снизить цену.
					else
					{
						newMainOrder = this.GetOrderByPriceLimit(jsonPrice, algoKey, myAlgMarketOrders, targetOrder, targetPrice, totalLimit, currentMarketSettings, bookAlgMarketOrders);
					}
				}
				else if (myMainOrder.Price > targetOrder.Price && myMainOrder.Price <= targetPrice)
				{
					//if (myMainOrder.Limit != mainOrderLimitSpeed)
					//	_ac.updateOrder(algoKey, myMainOrder.Id, myMainOrder.Price.ToString(new CultureInfo("en-US")), mainOrderLimitSpeed.ToString(new CultureInfo("en-US")));
					Console.WriteLine($"\t[{algoKey}]\tГлавный ордер стоит перед конкурирующим. Изменения не требуются.");
					newMainOrder = myMainOrder;
					targetPrice = this.NormalizeFloat(targetPrice, 4);
					newMainOrder.Price = targetPrice;
				}
				else
				{
					Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера ниже цены конкурирующего + шаг повышения ({priceRaiseStep}). Повышение цены.");
					targetPrice = this.NormalizeFloat(targetPrice, 4);
					myMainOrder.Price = targetPrice;
					//myMainOrder = _ac.updateOrder(algoKey, myMainOrder.Id, targetPrice.ToString(new CultureInfo("en-US")), mainOrderLimitSpeed.ToString(new CultureInfo("en-US")))?.Item1;
					//if (myMainOrder != null)
					//{
					//	Console.WriteLine($"\t[{algoKey}]\tСкорость ордера повышена до {targetPrice}");
					//	newMainOrder = myMainOrder;
					//}
					//else
					//	Console.WriteLine($"\t[{algoKey}]\tОшибка при повышении цены");
				}
			}
			else
			{
				newMainOrder = this.GetOrderByPriceLimit(jsonPrice, algoKey, myAlgMarketOrders, targetOrder, targetPrice, totalLimit, currentMarketSettings, bookAlgMarketOrders);
			}

			if (newMainOrder == null)
				newMainOrder = this.GetNextFreeOrder(algoKey, myAlgMarketOrders, targetPrice);
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

		private Order GetOrderByPriceLimit(float jsonPrice, string algoKey, List<Order> myAlgMarketOrders, BookOrder targetOrder, float targetPrice, float totalLimit, MarketSettings currentMarketSettings, List<BookOrder> bookAlgMarketOrders)
		{
			//var priceLimit = _settings.PriceLimitToFindOrder == 0.0f
			//	? targetPrice + Math.Abs(this.DownStepByAlgoritm[algoKey]) * _settings.MinStepsCountToFindOrder
			//	: targetPrice + _settings.PriceLimitToFindOrder;

			var priceLimit = _currentMarketSettings.OrdersSettings.PriceLimitToFindOrder;

			var myNextUpperOrder =
				myAlgMarketOrders
				.OrderByDescending(x => x.Price)
				.FirstOrDefault(x => x.Price >= targetOrder.Price && x.Price < priceLimit && x.Price < jsonPrice);

			if (myNextUpperOrder != null)
			{
				//var mainOrderLimitSpeed = this.GetMainOrderLimit(algoKey, totalLimit, currentMarketSettings, myAlgMarketOrders, bookAlgMarketOrders, targetOrder.Price, myNextUpperOrder.Limit, myNextUpperOrder.Id);

				Console.WriteLine($"\t[{algoKey}]\tНайден ордер с ценой {myNextUpperOrder.Price}");

				var newPrice = myNextUpperOrder.Price > targetPrice
					? this.NormalizeFloat(myNextUpperOrder.Price + this.DownStepByAlgoritm[algoKey])
					: myNextUpperOrder.Price;
				if (newPrice < targetPrice)
					newPrice = targetPrice;

				//var newLimit = mainOrderLimitSpeed;

				if (!CompareFloats(newPrice, myNextUpperOrder.Price, 4))
				{
					Console.WriteLine($"\t[{algoKey}]\tУстановка ордеру цены {newPrice}");
					//myNextUpperOrder = this.SetPrice(myNextUpperOrder, newPrice);
					myNextUpperOrder.Price = newPrice;
				}
				return myNextUpperOrder;
			}
			return null;
			//else
			//{
			//	Console.WriteLine($"\t[{algoKey}]\tОрдер в пределах {_settings.MinStepsCountToFindOrder} минимальных шагов / лимита цены {_settings.PriceLimitToFindOrder} не найден.");
			//	return null;
			//}
		}

		private void ProcessUpperOrder(List<string> processedOrderIds, string algoKey, List<Order> myAlgMarketOrders, float targetPrice)
		{
			var upperOrders = myAlgMarketOrders.Where(x => !processedOrderIds.Contains(x.Id) && x.Price > targetPrice).ToList();
			Console.WriteLine($"\t[{algoKey}]\tОбработка ордеров выше главного ({upperOrders.Count} шт)");
			upperOrders.ForEach(order =>
			{
				var price = order.Price + this.DownStepByAlgoritm[order.AlgorithmName];
				var limit = this.MinLimitByAlgoritm[order.AlgorithmName];

				if (!CompareFloats(price, order.Price, 4) || !CompareFloats(limit, order.Limit, 4))
					this.UpdateOrder(order, price, limit);
			});
		}

		private void ProcessLowerOrders(List<string> processedOrderIds, string algoKey, MarketSettings currentMarketSettings, List<Order> myAlgMarketOrders, float targetPrice)
		{
			var alg = _ac.algorithms.FirstOrDefault(x => x["algorithm"].ToString() == _settings.BotSettings.AlgorithmName);
			var minPrice = float.Parse(alg["minimalOrderAmount"].ToString(), CultureInfo.InvariantCulture);

			var lowerOrders = myAlgMarketOrders.Where(x => !processedOrderIds.Contains(x.Id) && x.Price <= targetPrice && x.Price > 0.0001f).ToList();
			Console.WriteLine($"\t[{algoKey}]\tОбработка ордеров ниже главного ({lowerOrders.Count} шт)");
			lowerOrders.ForEach(order =>
			{
				var updated = order;

				//if (!_settings.AllocationSettingsOn)
				//{
				//	if (order.Limit < currentMarketSettings.MaxLimitSpeed)
				//		updated = this.SetLimit(order, currentMarketSettings.MaxLimitSpeed);
				//	this.SetPrice(updated, updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName]);
				//}
				//else
				//{
				//var price = this.NormalizeFloat(order.Price, 4);
				//order.Price = price;
				
				var newPrice = updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName] < minPrice
						? minPrice
						: updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName];
				var limit = _currentMarketSettings.OrdersSettings.OtherOrdersLimitSettings;

				if (!CompareFloats(newPrice, order.Price,4) || !CompareFloats(limit, order.Limit, 4))
				{
					this.UpdateOrder(order, newPrice, limit);
				}


				//if (order.Limit != _currentMarketSettings.OrdersSettings.OtherOrdersLimitSettings)
				//	updated = this.SetLimit(order, _currentMarketSettings.OrdersSettings.OtherOrdersLimitSettings);

				//if (updated != null)
				//{
				//	var newPrice = updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName] < minPrice
				//		? minPrice
				//		: updated.Price + this.DownStepByAlgoritm[updated.AlgorithmName];

				//	this.SetPrice(updated, this.NormalizeFloat(newPrice, 4));
				//}
				//}
			});
		}

		//private float GetMainOrderLimit(string algoKey, float totalLimit, MarketSettings currentMarketSettings, List<Order> myAlgMarketOrders, List<BookOrder> bookAlgMarketOrders, float targetOrderPrice, float mainOrderLimitSpeed, string mainOrderId)
		//{
		//	if (currentMarketSettings.MaxLimitSpeedPercent > 0)
		//	{
		//		Console.WriteLine($"\t[{algoKey}]\tВключена настройка переопределения скорости для главного ордера");

		//		var upperOrdersPayingSpeedSum = bookAlgMarketOrders.Where(x => x.Alive && x.Price > targetOrderPrice).Sum(x => x.Limit);
		//		upperOrdersPayingSpeedSum += myAlgMarketOrders.Where(x => x.Id != mainOrderId && x.Price > targetOrderPrice).Sum(x => x.Limit);

		//		var ratio = this.NormalizeFloat(currentMarketSettings.MaxLimitSpeedPercent / 100 * totalLimit, 2);
		//		var calculatedLimit = this.NormalizeFloat(ratio - upperOrdersPayingSpeedSum);

		//		var minLimitByAlgoString = this.MinLimitByAlgoritm[algoKey].ToString().Replace(",",".");
		//		var parts = minLimitByAlgoString.Split('.');
		//		var decimals = parts[1].Length;

		//		calculatedLimit = this.NormalizeFloat(calculatedLimit, decimals);

		//		Console.WriteLine($"\t[{algoKey}]\tОбщая скорость маркета: {totalLimit}. Сумма лимитов ордеров выше главного: {upperOrdersPayingSpeedSum}. Процент от маркета из настройки: {ratio}.");

		//		Console.WriteLine($"\t[{algoKey}]\tРассчитанный лимит с учётом шага - [{calculatedLimit}]");

		//		if (calculatedLimit <= 0)
		//		{
		//			Console.WriteLine($"\t[{algoKey}]\tРассчитанный лимит меньше 0. Настройка не будет учитываться");
		//		}
		//		else
		//		{
		//			mainOrderLimitSpeed = calculatedLimit;
		//		}
		//	}
		//	else
		//		mainOrderLimitSpeed = currentMarketSettings.MaxLimitSpeed;

		//	return mainOrderLimitSpeed;
		//}

		private Order GetNextFreeOrder(string algoKey, List<Order> myAlgMarketOrders, float targetPrice)
		{
			Console.WriteLine($"\t[{algoKey}]\tПоиск ближайшего ордера снизу от конкурирующего");
			var myNextOrder = myAlgMarketOrders.FirstOrDefault(x => x.Price < targetPrice);
			if (myNextOrder != null)
			{
				//var mainOrderLimitSpeed = this.GetMainOrderLimit(algoKey, totalLimit, currentMarketSettings, myAlgMarketOrders, bookAlgMarketOrders, targetOrderPrice, maxLimitSpeed, myNextOrder.Id);

				Console.WriteLine($"\t[{algoKey}]\tНайден ордер {myNextOrder.Id}. Устанавливаем цену {targetPrice}");
				//myNextOrder = this.SetPrice(myNextOrder, targetPrice);
				myNextOrder.Price = targetPrice;
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

		private Order PatchOrder(Order order, float limit)
		{
			var alg = _ac.algorithms.FirstOrDefault(x => x["algorithm"].ToString() == _settings.BotSettings.AlgorithmName);
			var minPrice = float.Parse(alg["minimalOrderAmount"].ToString(), CultureInfo.InvariantCulture);

			var id = order.Id;
			if (order.Price < order.OldPrice && order.Price > minPrice)
			{
				var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
				var metadata = this.OrdersMetadataList.FirstOrDefault(x => x.Id == id);
				if (metadata == null)
				{
					metadata = new MyOrderMetadata { Id = order.Id, LastPriceDecreasedTime = 0 };
					this.OrdersMetadataList.Add(metadata);
				}
				var difference = currentTimeStamp - metadata.LastPriceDecreasedTime;
				if (difference > 615)
				{
					var price = this.NormalizeFloat(order.Price, 4);
					var updated = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
					metadata.LastPriceDecreasedTime = currentTimeStamp;
					return updated ?? this.SetLimit(order, limit);
				}
				else
					return this.SetLimit(order, limit);
			}
			else
			{
				var price = this.NormalizeFloat(order.Price, 4);
				var updated = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
				return updated;
			}
		}

		private Order UpdateOrder(Order order, float price, float limit)
		{
			var alg = _ac.algorithms.FirstOrDefault(x => x["algorithm"].ToString() == _settings.BotSettings.AlgorithmName);
			var minPrice = float.Parse(alg["minimalOrderAmount"].ToString(), CultureInfo.InvariantCulture);

			var id = order.Id;
			if (price < order.Price && price > minPrice)
			{
				var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
				var metadata = this.OrdersMetadataList.FirstOrDefault(x => x.Id == id);
				if (metadata == null)
				{
					metadata = new MyOrderMetadata { Id = order.Id, LastPriceDecreasedTime = 0 };
					this.OrdersMetadataList.Add(metadata);
				}
				var difference = currentTimeStamp - metadata.LastPriceDecreasedTime;
				if (difference > 615)
				{
					price = this.NormalizeFloat(price, 4);
					var updated = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
					metadata.LastPriceDecreasedTime = currentTimeStamp;
					return updated ?? this.SetLimit(order, limit);
				}
				else
					return this.SetLimit(order, limit);
			}
			else
			{
				price = this.NormalizeFloat(price, 4);
				var updated = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
				return updated;
			}
		}

		private Order SetLimit(Order order, float limit = 0)
		{
			if (!CompareFloats(order.Limit, limit, 6))
			{
				var temp = order;
				order = _ac.updateOrder(order.AlgorithmName, order.Id, order.Price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
				if (order == null)
				{
					Thread.Sleep(2000);
					order = _ac.updateOrder(temp.AlgorithmName, temp.Id, temp.Price.ToString(new CultureInfo("en-US")), limit.ToString(new CultureInfo("en-US")))?.Item1;
				}
			}
			return order;
		}

		private Order SetPrice(Order order, float price)
		{
			var id = order.Id;

			var alg = _ac.algorithms.FirstOrDefault(x => x["algorithm"].ToString() == _settings.BotSettings.AlgorithmName);
			var minPrice = float.Parse(alg["minimalOrderAmount"].ToString(), CultureInfo.InvariantCulture);

			if (price < order.Price && price > minPrice)
			{
				var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();
				var metadata = this.OrdersMetadataList.FirstOrDefault(x => x.Id == id);
				if (metadata == null)
				{
					metadata = new MyOrderMetadata { Id = order.Id, LastPriceDecreasedTime = 0 };
					this.OrdersMetadataList.Add(metadata);
				}
				var difference = currentTimeStamp - metadata.LastPriceDecreasedTime;
				if (difference > 615)
				{
					price = this.NormalizeFloat(price, 4);
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

			price = this.NormalizeFloat(price, 4);
			if (!CompareFloats(price, order.Price, 4))
				order = _ac.updateOrder(order.AlgorithmName, order.Id, price.ToString(new CultureInfo("en-US")), order.Limit.ToString(new CultureInfo("en-US")))?.Item1;
			return order;
		}

		private List<BookOrder> GetAllOrders(List<string> marketNames, List<string> myMarketNames)
		{
			var allBookOrders = new List<BookOrder>();

			//var algorithmName = jAlgorithm["algorithm"].ToString();
			//if (algorithmName.ToLowerInvariant() != _botSettings.AlgorithmName.ToLowerInvariant()) continue;

			var jOrders = _ac.getOrderBookWebRequest(_settings.BotSettings.AlgorithmName);

			var jStats = jOrders["stats"];

			foreach (var marketName in marketNames)
			{
				if (!myMarketNames.Contains(marketName)) continue;

				var jMarketOrders = jStats[marketName]?["orders"];

				var totalSpeed = jStats[marketName]?["totalSpeed"].ToString();
				var key = $"{_settings.BotSettings.AlgorithmName}.{marketName}";
				if (totalSpeed != null && !this.TotalSpeedByMarket.ContainsKey(key))
					this.TotalSpeedByMarket.Add(key, 0);
				this.TotalSpeedByMarket[key] = (float)Math.Round(Convert.ToDouble(totalSpeed, new CultureInfo("en-US")), 4);

				if (jMarketOrders != null)
				{
					var marketBookOrders = JsonConvert.DeserializeObject<List<BookOrder>>(jMarketOrders.ToString());
					marketBookOrders.ForEach(x => { x.MarketName = marketName; x.AlgorithmName = _settings.BotSettings.AlgorithmName; });
					allBookOrders.AddRange(marketBookOrders);
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
				myOrder.PoolId = Guid.Parse(jOrder["pool"]["id"].ToString());
				myOrder.PoolName = jOrder["pool"]["name"].ToString();
				myOrder.Active = jOrder["status"]["code"].ToString() == "ACTIVE";
				myOrder.RigsCount = int.Parse(jOrder["rigsCount"].ToString());
				myOrder.OldPrice = myOrder.Price;
				if (myOrder.AlgorithmName.ToLowerInvariant() == _settings.BotSettings.AlgorithmName.ToLowerInvariant() && myOrder.Active)
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
			Console.WriteLine("\nНачало логики пополнения");
			var settings = _currentMarketSettings.RefillSettings;

			foreach (var order in myOrders)
			{
				if (order.AcceptedCurrentSpeed == 0) continue;
				Console.WriteLine($"Обработка ордера {order.Id}");

				var spendingPerMinute = order.Price * order.AcceptedCurrentSpeed / 1440;
				var targetAmount = spendingPerMinute * settings.RefillOrderLimit;
				var orderAmount = order.AvailableAmount - order.PayedAmount;

				Console.WriteLine($"Ордер тратит в минуту: {spendingPerMinute}");
				Console.WriteLine($"Умножение на {nameof(settings.RefillOrderLimit)}: {spendingPerMinute} * {settings.RefillOrderLimit} = {targetAmount}");
				Console.WriteLine($"Баланс ордера: {orderAmount}");

				if (orderAmount < targetAmount)
				{
					// Умножаем потребление ордера на количество минут, которое необходимо ему проработать после пополнения
					var targetBalance = spendingPerMinute * settings.RefillOrderAmount;
					// Отнимаем от нужного баланса текущий баланс ордера
					targetBalance = targetBalance - orderAmount;
					// Прибавляем к полученному значению комиссию найсхеша 3%
					targetBalance = targetBalance * 1.03f;
					targetBalance = this.NormalizeFloat(targetBalance, 8);

					Console.WriteLine($"Сумма пополнения: {targetBalance}");

					if (targetBalance < 0.001f)
						targetBalance = 0.001f;

					_ac.refillOrder(order.Id, targetBalance.ToString(new CultureInfo("en-US")));
				}
				else
					Console.WriteLine("Ордер не нуждается в пополнении.");

			}

			Console.WriteLine("Окончание логики пополнения\n");
			//var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeSeconds();

			//if (_iteration == 1)
			//lastRefillRunStamp = currentTimeStamp;

			//if ((currentTimeStamp - lastRefillRunStamp) >= _botSettings.RunRefillDelay)
			//{
			//myOrders.ForEach(order =>
			//	{
			//		var ok = ((order.PayedAmount > _botSettings.RefillPayedAmountLimit) && ((order.AvailableAmount - order.PayedAmount) < _botSettings.RefillOrderLimit));
			//		if (ok)
			//			_ac.refillOrder(order.Id, _botSettings.RefillOrderAmount.ToString(new CultureInfo("en-US")));
			//	});
			//lastRefillRunStamp = currentTimeStamp;
			//}
		}

		private void FormOrdersGroup(List<Order> orders, List<string> processedOrderIds, string algoKey, MarketSettings currentMarketSettings, Order mainOrder)
		{
			var targetPrice = mainOrder.Price;
			orders = orders.Where(x => x.Id != mainOrder.Id).ToList();

			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine();
			Console.WriteLine($"\t[{algoKey}]\t***");
			Console.WriteLine($"\t[{algoKey}]\tФормирование группы ордеров после главного ордера");
			Console.ForegroundColor = ConsoleColor.White;

			var groupOrders = new List<Order>();

			var allocationSettings = _currentMarketSettings.OrdersSettings.AllocationSettings;
			var allocationSettingsSum = allocationSettings.Sum(x => x.PriceStepLow);

			var minAllowedPrice = this.NormalizeFloat(targetPrice - allocationSettingsSum);

			Console.WriteLine($"\t[{algoKey}]\tЦена главного ордера: {targetPrice}. Минимально допустимая цена группы: {minAllowedPrice}");

			var ordersBetween = orders.Where(x => x.Id != mainOrder.Id && x.Price <= targetPrice && x.Price >= minAllowedPrice).OrderByDescending(x => x.Price).ToList();

			Console.WriteLine($"\t[{algoKey}]\tКоличество ордеров в диапазоне [{targetPrice}]-[{minAllowedPrice}] : {ordersBetween.Count}");

			var targetPosition = 2;
			//var previousOrderPrice = targetPrice;
			foreach (var order in ordersBetween)
			{
				Console.WriteLine($"\t[{algoKey}]\tОбработка ордера с Id [{order.Id}]. Цена: [{order.Price}]. Цена предыдущего ордера: [{targetPrice}]");

				var limitSetting = allocationSettings.FirstOrDefault(x => x.Position == targetPosition);

				var updated = order;

				var delta = this.NormalizeFloat(targetPrice - order.Price);
				if (delta > limitSetting.PriceStepLow)
				{
					var newPrice = this.NormalizeFloat(targetPrice - limitSetting.PriceStepLow);
					//var newLimit = order.Limit != limitSetting.LimitSpeed ? limitSetting.LimitSpeed : order.Limit;

					var floatsEqual = CompareFloats(newPrice, order.Price, 4);
					if (!floatsEqual)
					{
						Console.WriteLine($"\t[{algoKey}]\tДельта с ценой предыдущего ордера выше {limitSetting.PriceStepLow}. Повышаем цену до {newPrice}");
						updated.Price = newPrice;


						//updated = this.UpdateOrder(order, newPrice, newLimit);
						//updated = this.SetPrice(order, newPrice);
						//if (updated == null)
						//{
						//	Thread.Sleep(2500);
						//	updated = this.SetPrice(order, newPrice);
						//}
					}
				}
				else if (delta < limitSetting.PriceStepLow)
				{
					var desiredPrice = this.NormalizeFloat(targetPrice - limitSetting.PriceStepLow, 4);
					var step = this.DownStepByAlgoritm[order.AlgorithmName];

					if (this.NormalizeFloat(order.Price + step, 4) > desiredPrice)
					{
						updated.Price = order.Price + step;
						//this.SetPrice(order, order.Price + step);
					}
					else
						updated.Price = desiredPrice;
						//this.SetPrice(order, desiredPrice);
				}

				groupOrders.Add(updated);
				targetPosition++;
				//previousOrderPrice = updated.Price;

				//if (updated.Limit != limitSetting.LimitSpeed)
				//	this.SetLimit(order, limitSetting.LimitSpeed);

				if (groupOrders.Count == _currentMarketSettings.OrdersSettings.Quantity - 1) break;
			}

			groupOrders = groupOrders.Take(_currentMarketSettings.OrdersSettings.Quantity).ToList();
			Console.WriteLine($"\t[{algoKey}]\tЗакончена обработка ордеров в диапазоне [{targetPrice}]-[{minAllowedPrice}]");

			processedOrderIds.AddRange(groupOrders.Select(x => x.Id));

			if (groupOrders.Count < _currentMarketSettings.OrdersSettings.Quantity - 1)
			{
				Console.WriteLine($"\t[{algoKey}]\tКоличество ордеров в группе главных меньше {_currentMarketSettings.OrdersSettings.Quantity}. Поиск ордеров с меньшей стоимостью.");

				//previousOrderPrice = groupOrders.Any() ? groupOrders.Last().Price : targetPrice;

				var lowerOrders = orders.Where(x => x.Price <= targetPrice && !processedOrderIds.Contains(x.Id)).OrderByDescending(x => x.Price).ToList();
				Console.WriteLine($"\t[{algoKey}]\tКоличество найденных ордеров с меньшей стоимостью: {lowerOrders.Count}");

				foreach (var lowerOrder in lowerOrders)
				{
					var limitSetting = allocationSettings.FirstOrDefault(x => x.Position == targetPosition);
					if (limitSetting != null)
					{
						var groupOrderTargetPrice = this.NormalizeFloat(targetPrice - limitSetting.PriceStepLow);

						var newPrice = groupOrderTargetPrice;
						//var newLimit = lowerOrder.Limit != limitSetting.LimitSpeed ? limitSetting.LimitSpeed : lowerOrder.Limit;

						var pricesEqual = CompareFloats(lowerOrder.Price, newPrice, 4);

						if (!pricesEqual)
						{
							Console.WriteLine($"\t[{algoKey}]\tОбработка ордера с Id [{lowerOrder.Id}] и ценой [{lowerOrder.Price}]. Установка цены {newPrice}");

							//var updated = this.SetPrice(lowerOrder, newPrice);
							lowerOrder.Price = newPrice;
							//if (updated != null)
							//{
								groupOrders.Add(lowerOrder);
								//previousOrderPrice = groupOrderTargetPrice;
							//}
							//else
							//	this.WarnConsole($"\t[{algoKey}]\tНе удалось обработать ордер с Id [{lowerOrder.Id}]");
						}
					}
					if (groupOrders.Count == _currentMarketSettings.OrdersSettings.Quantity - 1) break;
					targetPosition++;
				}
			}

			processedOrderIds.AddRange(groupOrders.Select(x => x.Id));

			_activeOrders.AddRange(groupOrders);

			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine($"\t[{algoKey}]\tФормирование группы ордеров после главного ордера завершено\n");
			Console.ForegroundColor = ConsoleColor.White;
		}

		private float GetBalance()
		{
			var result = float.MinValue;
			if (_ac.connected)
			{
				JObject balance = _ac.getBalance(_currency);
				if (balance != null)
				{
					result = float.Parse(balance["available"].ToString(), CultureInfo.InvariantCulture);
				}
			}
			if (result == float.MinValue)
			{
				this.WarnConsole("Не удалось получить баланс аккаунта по API");
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

		private List<Pool> GetPools()
		{
			var list = new List<Pool>();
			foreach (var jPool in _ac.pools)
			{
				list.Add(new Pool
				{
					Id = Guid.Parse(jPool["id"].ToString()),
					Name = jPool["name"].ToString()
				});
			}
			return list;
		}
	}
}