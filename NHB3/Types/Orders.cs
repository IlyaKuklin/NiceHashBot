using System;

namespace NHB3.Types
{
	public abstract class OrderBase
	{
		public string Id { get; set; }
		public float Price { get; set; }
		public float Limit { get; set; }
		public bool Alive { get; set; }
		public bool Active { get; set; }
		public string MarketName { get; set; }
		public string AlgorithmName { get; set; }
		public int RigsCount { get; set; }
	}

	public class BookOrder : OrderBase
	{
		public string Type { get; set; }
		public float AcceptedSpeed { get; set; }
		public float PayingSpeed { get; set; }
	}

	public class Order : OrderBase
	{
		public float Amount { get; set; }
		public float AcceptedCurrentSpeed { get; set; }
		public float AvailableAmount { get; set; }
		public float PayedAmount { get; set; }
		public Guid PoolId { get; set; }
		public string PoolName { get; set; }
	}
}