namespace MiNET.Items.Enchantments
{
	public class Power : Enchantment
	{
		public Power(short level = 1) : base(level)
		{
			Id = EnchantmentType.Power;
		}
	}
}