using System.Collections.Generic;

namespace NHB3.Types
{
	public class BotSettings
	{
		/** Актуальные. */

		// Частота запуска логики бота (в секундах).
		public int RunBotDelay { get; set; }

		// url настроек цены.
		public string JsonSettingsUrl { get; set; }

		public int MinStepsCountToFindOrder { get; set; }

		public int RunRefillDelay { get; set; }

		// После какого остатка делать refill.
		public float RefillOrderLimit { get; set; }

		// Объём refill.
		public float RefillOrderAmount { get; set; }

		public string TgBotToken { get; set; }

		public string TgChatId { get; set; }

		public int ErrorDelay { get; set; }

		public float JsonPrice { get; set; }

		public List<BotMarketSettings> MarketSettings { get; set; }
	}

	public class BotMarketSettings
	{
		public string Name { get; set; }

		// Какую скорость устанавливать при перебитии ордера.
		public float MaxLimitSpeed { get; set; }
	}
}