using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace NHB3.Types
{
	public class BotSettings
	{
		public string AlgorithmName { get; set; }

		// Частота запуска логики бота (в секундах).
		public int RunBotDelay { get; set; }

		// url настроек цены.
		public string JsonSettingsUrl { get; set; }

		public int MinStepsCountToFindOrder { get; set; }

		public float PriceLimitToFindOrder { get; set; }

		public int RunRefillDelay { get; set; }

		// После какого остатка делать refill.
		public float RefillOrderLimit { get; set; }

		// Объём refill.
		public float RefillOrderAmount { get; set; }

		// С какого затраченного амаунта начинать рефилл
		public float RefillPayedAmountLimit { get; set; }

		public string TgBotToken { get; set; }

		public string TgChatId { get; set; }

		public int ErrorDelay { get; set; }

		public string ErrorUrlHandler { get; set; }

		public float JsonPrice { get; set; }

		public List<BotMarketSettings> MarketSettings { get; set; }

		public bool AllocationSettingsOn { get; set; }

		/// <summary>
		/// Минимальный баланс, при котором отрабатывает логика бота.
		/// </summary>
		public float MinBalanceToRunBot { get; set; }
	}

	public class BotMarketSettings
	{
		public string Name { get; set; }

		// Какую скорость устанавливать при перебитии ордера.
		public float MaxLimitSpeed { get; set; }

		public AllocationSettings AllocationSettings { get; set; }

		public float PriceRaiseStep { get; set; }
		public float MaxLimitSpeedPercent { get; set; }

		public float LowerOrdersLimitPriceRange { get; set; }

		public float LowerOrdersLimitThreshold { get; set; }

		public int LowerOrdersLogicRunCycleGap { get; set; }

		public RivalOrderDetectionSettings RivalOrderDetectionSettings { get; set; }

		public EmptyOrderSettings EmptyOrderSettings { get; set; }

		[JsonIgnore]
		internal bool LowerOrdersSlowedDown { get; set; }
	}

	public class AllocationSettings
	{
		public int ProcessedOrdersCount { get; set; }
		public float OtherOrdersLimitSettings { get; set; }
		public List<AllocationLimitSettings> LimitSettings { get; set; }
	}

	public class AllocationLimitSettings
	{
		public string OrdersPositions { get; set; }
		public float MaxLimitSpeed { get; set; }
		public float PriceStep { get; set; }

		[JsonIgnore]
		public List<int> OrdersPositionsList
		{
			get
			{
				var list = new List<int>();
				var parts = this.OrdersPositions.Split(',');
				list.AddRange(parts.Select(part => int.Parse(part)));
				return list;
			}
		}
	}

	public class RivalOrderDetectionSettings
	{
		public bool On { get; set; }
		public float MinLimit { get; set; }
		public float MaxLimit { get; set; }
	}

	public class EmptyOrderSettings
	{
		public int Quantity { get; set; }
		public bool PoolNameOrders { get; set; }
		public int PoolNameOrdersStart { get; set; }
		public int PoolNameOrdersEnd { get; set; }
		public int Amount { get; set; }
		public float Limit { get; set; }
		public float Price { get; set; }
	}
}