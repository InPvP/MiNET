namespace MiNET.Items.Enchantments
{
	public class Fortune : Enchantment
	{
		public Fortune(short level = 1) : base(level)
		{
			Id = EnchantmentType.Fortune;
		}
	}
}