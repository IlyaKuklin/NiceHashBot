using Newtonsoft.Json;
using System.Collections.Generic;

namespace NHB3.Types
{
	public class Settings
	{
		public BotSettings BotSettings { get; set; }
		public AlertSettings AlertSettings { get; set; }
		public List<MarketSettings> MarketSettings { get; set; }
		public OrderCancellationSettings CancellationSettings { get; set; }

	}

	public class AlertSettings
	{
		public string TgBotToken { get; set; }
		public string TgChatId { get; set; }
		public string ErrorUrlHandler { get; set; }
	}

	public class BotSettings
	{
		public string AlgorithmName { get; set; }
		public int RunBotDelay { get; set; }
		public int ErrorDelay { get; set; }
		public string JsonSettingsUrl { get; set; }
		public float MinBalanceToRunBot { get; set; }
		public int MinBalanceCheckInterval { get; set; }

		public float JsonPrice { get; set; }

		//public int MinStepsCountToFindOrder { get; set; }

		//public float JsonPrice { get; set; }


		//public OrderRefillSettings RefillSettings { get; set; }

		//public bool AllocationSettingsOn { get; set; }
	}

	public class MarketSettings
	{
		public string Name { get; set; }

		public OrderSettings OrdersSettings { get; set; }
		public LowerOrdersSettings LowerOrdersSettings { get; set; }

		public RivalOrderDetectionSettings RivalOrderDetectionSettings { get; set; }

		public EmptyOrderSettings EmptyOrderSettings { get; set; }

		public OrderRefillSettings RefillSettings { get; set; }

		[JsonIgnore]
		internal bool LowerOrdersSlowedDown { get; set; }
	}

	public class OrderSettings
	{
		public int Quantity { get; set; } // количество рабочих ордеров
		public string LimitFunction { get; set; } // функция, которая будет отвечать за скорость ордерам
		public float SummaryCurrentSpeed { get; set; }
		public float OtherOrdersLimitSettings { get; set; }
		public float PriceLimitToFindOrder { get; set; }
		public List<AllocationSettings> AllocationSettings { get; set; }
	}

	public class OrderCancellationSettings
	{
		public bool On { get; set; }
		public int Interval { get; set; }
		public float AcceptedCurrentSpeedThreshold { get; set; }
		public float JsonPriceExcessThreshold { get; set; }
	}

	public class OrderRefillSettings
	{
		public int RefillOrderLimit { get; set; }
		public int RefillOrderAmount { get; set; }
	}

	public class AllocationSettings
	{
		public int Position { get; set; } // позиция ордера
		public float Limit { get; set; } // для функции summaryCurrentSpeed у всех ордеров должно быть одно значение.
		public float LimitSpeed { get; set; }
		public bool Fight { get; set; }
		public float PriceRaiseStep { get; set; } // на сколько перебивать чужой ордер, игнорируется, если "fight" false
		public float PriceStepHigh { get; set; } // игнорируется для ордера с позицией 1
		public float PriceStepLow { get; set; } // игнорируется для ордера с позицией 1
	}

	//public class AllocationLimitSettings
	//{
	//	public string OrdersPositions { get; set; }
	//	public float MaxLimitSpeed { get; set; }
	//	public float PriceStep { get; set; }

	//	[JsonIgnore]
	//	public List<int> OrdersPositionsList
	//	{
	//		get
	//		{
	//			var list = new List<int>();
	//			var parts = this.OrdersPositions.Split(',');
	//			list.AddRange(parts.Select(part => int.Parse(part)));
	//			return list;
	//		}
	//	}
	//}

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
		public float Amount { get; set; }
		public float Limit { get; set; }
		public float Price { get; set; }
	}

	public class LowerOrdersSettings
	{
		public float LowerOrdersLimitPriceRange { get; set; }
		public float LowerOrdersLimitThreshold { get; set; }
		public int LowerOrdersLogicRunCycleGap { get; set; }
	}
}