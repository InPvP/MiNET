namespace MiNET.Items.Enchantments
{
	public class Durability : Enchantment
	{
		public Durability(short level = 1) : base(level)
		{
			Id = EnchantmentType.Durability;
		}
	}
}
